using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

#if !NETCORE
using TomLonghurst.RedisClient.Extensions;
#endif

namespace TomLonghurst.RedisClient.Pipes
{
    public class StreamPipe : IDuplexPipe
    {
        public static IDuplexPipe GetDuplexPipe(Stream stream, PipeOptions sendPipeOptions,
            PipeOptions receivePipeOptions)
            => new StreamPipe(stream, sendPipeOptions, receivePipeOptions, true, true);

        private readonly Stream _innerStream;

        private readonly Pipe _readPipe;
        private readonly Pipe _writePipe;

        public StreamPipe(Stream stream, PipeOptions sendPipeOptions, PipeOptions receivePipeOptions, bool read,
            bool write)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            if (sendPipeOptions == null)
            {
                sendPipeOptions = PipeOptions.Default;
            }

            if (receivePipeOptions == null)
            {
                receivePipeOptions = PipeOptions.Default;
            }

            _innerStream = stream;

            if (!(read || write))
            {
                throw new ArgumentException("At least one of read/write must be set");
            }

            if (read)
            {
                if (!stream.CanRead)
                {
                    throw new InvalidOperationException("Cannot create a read pipe over a non-readable stream");
                }
                
                receivePipeOptions.ReaderScheduler.Schedule(
                    async obj => await ((StreamPipe) obj).CopyFromStreamToReadPipe().ConfigureAwait(false), this);
                
                _readPipe = new Pipe(receivePipeOptions);
            }


            if (write)
            {
                if (!stream.CanWrite)
                {
                    throw new InvalidOperationException("Cannot create a write pipe over a non-writable stream");
                }
                
                sendPipeOptions.WriterScheduler.Schedule(
                    async obj => await ((StreamPipe) obj).CopyFromWritePipeToStream().ConfigureAwait(false), this);
                
                _writePipe = new Pipe(sendPipeOptions);
            }
        }

        public PipeWriter Output =>
            _writePipe?.Writer ?? throw new InvalidOperationException("Cannot write to this pipe");

        public PipeReader Input =>
            _readPipe?.Reader ?? throw new InvalidOperationException("Cannot read from this pipe");

        private async Task CopyFromStreamToReadPipe()
        {
            Exception exception = null;
            var writer = _readPipe.Writer;

            try
            {
                while (true)
                {
                    try
                    {
                        var memory = writer.GetMemory(512);
#if NETCORE
                    var bytesRead = await _innerStream.ReadAsync(memory);
#else
                        var arr = memory.GetArraySegment();

                        var bytesRead = await _innerStream.ReadAsync(arr.Array, arr.Offset, arr.Count)
                            .ConfigureAwait(false);
#endif

                        if (bytesRead == 0)
                        {
                            break;
                        }

                        writer.Advance(bytesRead);

                        var result = await writer.FlushAsync().ConfigureAwait(false);

                        if (result.IsCompleted || result.IsCanceled)
                        {
                            break;
                        }
                    }
                    catch (IOException)
                    {
                        // TODO Why does this occur?
                        //    "Unable to read data from the transport connection: The I/O operation has been aborted because of either a thread exit or an application request."
                    }
                }
            }
            catch (Exception e)
            {
                exception = e;
            }

            writer.Complete(exception);
        }

        private long _totalBytesSent, _totalBytesReceived;

        //long IMeasuredDuplexPipe.TotalBytesSent => Interlocked.Read(ref _totalBytesSent);
        //long IMeasuredDuplexPipe.TotalBytesReceived => Interlocked.Read(ref _totalBytesReceived);

        private async Task CopyFromWritePipeToStream()
        {
            Exception exception = null;
            var reader = _writePipe.Reader;

            try
            {
                while (true)
                {
                    var pendingReadResult = reader.ReadAsync();

                    if (!pendingReadResult.IsCompleted)
                    {
                        await _innerStream.FlushAsync().ConfigureAwait(false);
                    }

                    var readResult = await pendingReadResult.ConfigureAwait(false);

                    do
                    {
                        if (!readResult.Buffer.IsEmpty)
                        {
                            if (readResult.Buffer.IsSingleSegment)
                            {
                                var writeTask = WriteSingle(readResult.Buffer);
                                if (!writeTask.IsCompleted)
                                {
                                    await writeTask.ConfigureAwait(false);
                                }
                            }
                            else
                            {
                                var writeTask = WriteMultiple(readResult.Buffer);
                                if (!writeTask.IsCompleted)
                                {
                                    await writeTask.ConfigureAwait(false);
                                }
                            }
                        }

                        reader.AdvanceTo(readResult.Buffer.End);

                    } while (!(readResult.Buffer.IsEmpty && readResult.IsCompleted)
                             && reader.TryRead(out readResult));

                    if ((readResult.IsCompleted || readResult.IsCanceled) && readResult.Buffer.IsEmpty)
                    {
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                exception = e;
            }
            finally
            {
                reader.Complete(exception);
            }
        }

        private Task WriteSingle(ReadOnlySequence<byte> buffer)
        {
#if NETCORE
            var valueTask = _innerStream.WriteAsync(buffer.First);
            return valueTask.IsCompletedSuccessfully ? Task.CompletedTask : valueTask.AsTask();
#else
            var arr = buffer.First.GetArraySegment();
            return _innerStream.WriteAsync(arr.Array, arr.Offset, arr.Count);
#endif
        }

        private async Task WriteMultiple(ReadOnlySequence<byte> buffer)
        {
            foreach (var segment in buffer)
            {
#if NETCORE
                await _innerStream.WriteAsync(segment);
#else
                var arraySegment = segment.GetArraySegment();
                await _innerStream
                    .WriteAsync(arraySegment.Array, arraySegment.Offset, arraySegment.Count)
                    .ConfigureAwait(false);
#endif
            }
        }
    }
}