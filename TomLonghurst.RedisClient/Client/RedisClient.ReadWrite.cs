﻿using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TomLonghurst.RedisClient.Exceptions;
using TomLonghurst.RedisClient.Extensions;
using TomLonghurst.RedisClient.Helpers;

namespace TomLonghurst.RedisClient.Client
{
    public partial class RedisClient : IDisposable
    {
        private static readonly Logger Log = new Logger();

        private readonly SemaphoreSlim _sendSemaphoreSlim = new SemaphoreSlim(1, 1);

        private long _outStandingOperations;

        public long OutstandingOperations => Interlocked.Read(ref _outStandingOperations);

        private long _operationsPerformed;

        private IDuplexPipe _pipe;
        private ReadResult _readResult;

        internal string LastAction;

        public long OperationsPerformed => Interlocked.Read(ref _operationsPerformed);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async ValueTask<T> SendAndReceiveAsync<T>(string command,
            Func<ValueTask<T>> responseReader,
            CancellationToken cancellationToken,
            bool isReconnectionAttempt = false)
        {
            CancellationTokenSource cancellationTokenSourceWithTimeout = null;

            try
            {
                LastAction = "Throwing Cancelled Exception due to Cancelled Token";
                cancellationToken.ThrowIfCancellationRequested();
                
                cancellationTokenSourceWithTimeout = CancellationTokenHelper.CancellationTokenWithTimeout(ClientConfig.Timeout,
                    cancellationToken);

                var cancellationTokenWithTimeout = cancellationTokenSourceWithTimeout.Token;

                Interlocked.Increment(ref _outStandingOperations);


                if (!isReconnectionAttempt)
                {
                    LastAction = "Waiting for Send/Receive lock to be free";
                    await _sendSemaphoreSlim.WaitAsync(cancellationTokenWithTimeout).ConfigureAwait(false);
                }

                Log.Debug($"Executing Command: {command}");
                LastCommand = command;

                Interlocked.Increment(ref _operationsPerformed);

                if (!isReconnectionAttempt)
                {
                    await TryConnectAsync(cancellationTokenWithTimeout).ConfigureAwait(false);
                }

                await Write(command);

                LastAction = "Reading Bytes Async";
                _readResult = await _pipe.Input.ReadAsync().ConfigureAwait(false);

                return await responseReader.Invoke();
            }
            catch (OperationCanceledException operationCanceledException)
            {
                throw TimeoutOrCancelledException(operationCanceledException, cancellationToken);
            }
            catch (SocketException socketException)
            {
                if (socketException.InnerException?.GetType().IsAssignableFrom(typeof(OperationCanceledException)) ==
                    true)
                {
                    throw TimeoutOrCancelledException(socketException.InnerException, cancellationToken);
                }

                throw;
            }
            catch (Exception innerException)
            {
                if (innerException.IsSameOrSubclassOf(typeof(RedisException)) ||
                    innerException.IsSameOrSubclassOf(typeof(OperationCanceledException)))
                {
                    throw;
                }

                DisposeNetwork();
                IsConnected = false;
                throw new RedisConnectionException(innerException);
            }
            finally
            {
                Interlocked.Decrement(ref _outStandingOperations);
                if (!isReconnectionAttempt)
                {
                    LastAction = "Releasing Send/Receive Lock";
                    _sendSemaphoreSlim.Release();
                }

                cancellationTokenSourceWithTimeout?.Dispose();
            }
        }

        private ValueTask<FlushResult> Write(string command)
        {
            var encodedCommand = command.ToRedisProtocol();
            
            LastAction = "Writing Bytes";
            var pipeWriter = _pipe.Output;
#if NETCORE
            var charsSpan = encodedCommand.AsSpan();
            
            var bytesSpan = pipeWriter.GetSpan(Encoding.UTF8.GetByteCount(charsSpan));

            var bytesCount = Encoding.UTF8.GetBytes(charsSpan, bytesSpan);
            
            pipeWriter.Advance(bytesCount);
            
            return _pipe.Output.FlushAsync();
#else
            return pipeWriter.WriteAsync(encodedCommand.ToUtf8Bytes().AsMemory());
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async ValueTask<Memory<byte>> ReadData(bool readToEnd = false)
        {
            var buffer = _readResult.Buffer;

            if (buffer.IsEmpty && _readResult.IsCompleted)
            {
                throw new UnexpectedRedisResponseException("Zero Length Response from Redis");
            }

            var line = await ReadLine();
            
            var firstChar = line.First();

            if (string.IsNullOrEmpty(line))
            {
                throw new Exception("No data to process");
            }

            if (firstChar == '-')
            {
                throw new RedisFailedCommandException(line, LastCommand);
            }

            if (firstChar == '$')
            {
                if (line == "$-1")
                {
                    return null;
                }
                
                LastAction = "Reading Data Synchronously in ReadData";
                if (!_pipe.Input.TryRead(out _readResult))
                {
                    LastAction = "Reading Data Asynchronously in ReadData";
                    _readResult = await _pipe.Input.ReadAsync().ConfigureAwait(false);
                }
                
                buffer = _readResult.Buffer;

                if (long.TryParse(line.Substring(1), out var byteSizeOfData))
                {
                    var bytes = new byte[byteSizeOfData].AsMemory();
                    
                    buffer = buffer.Slice(0, Math.Min(byteSizeOfData, buffer.Length));
                    
                    var bytesReceived = buffer.Length;

                    buffer.CopyTo(bytes.Slice(0, (int) bytesReceived).Span);

                    while (bytesReceived < byteSizeOfData)
                    {
                        LastAction = "Advancing Buffer in ReadData Loop";
                        _pipe.Input.AdvanceTo(buffer.End);

                        LastAction = "Reading Data Synchronously in ReadData Loop";
                        if (!_pipe.Input.TryRead(out _readResult))
                        {
                            LastAction = "Reading Data Asynchronously in ReadData Loop";
                            _readResult = await _pipe.Input.ReadAsync().ConfigureAwait(false);
                        }
                        
                        buffer = _readResult.Buffer;
                        
                        buffer.Slice(0, Math.Min(buffer.Length, byteSizeOfData - bytesReceived))
                            .CopyTo(bytes.Slice((int) bytesReceived, (int) Math.Min(buffer.Length, byteSizeOfData - bytesReceived)).Span);
                        
                        bytesReceived += buffer.Length;
                    }

                    if (readToEnd)
                    {
                        LastAction = "Advancing Buffer to End of Buffer";
                        _pipe.Input.AdvanceTo(_readResult.Buffer.End);
                    }
                    else
                    {
                        LastAction = "Advancing Buffer to Line Terminator";
                        _readResult = await _pipe.Input.AdvanceToLineTerminator(_readResult);
                    }

                    return bytes;
                }

                throw new UnexpectedRedisResponseException("Invalid length");
            }

            throw new UnexpectedRedisResponseException($"Unexpected reply: {line}");
        }

        private async ValueTask<string> ReadLine()
        {
            LastAction = "Reading until End of Line found";
            _readResult = await _pipe.Input.ReadUntilEndOfLineFound(_readResult);

            LastAction = "Finding End of Line Position";
            var endOfLineAfterByteCount = _readResult.Buffer.GetEndOfLinePosition();

            if (endOfLineAfterByteCount == null)
            {
                throw new Exception("Can't find EOL");
            }

            var buffer = _readResult.Buffer;

            buffer = buffer.Slice(0, endOfLineAfterByteCount.Value);

            // Reslice but removing the line terminators
            var line = buffer.Slice(0, buffer.Length - 2).AsString();

            LastAction = "Advancing Buffer to End of Line";
            _pipe.Input.AdvanceTo(endOfLineAfterByteCount.Value);

            return line;
        }

        private Exception TimeoutOrCancelledException(Exception exception, CancellationToken originalCancellationToken)
        {
            if (originalCancellationToken.IsCancellationRequested)
            {
                throw exception;
            }

            throw new RedisOperationTimeoutException(this);
        }
    }
}