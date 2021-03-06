using System;
using System.Threading;
using System.Threading.Tasks;
using TomLonghurst.RedisClient.Constants;

namespace TomLonghurst.RedisClient.Client
{
    public partial class RedisClient : IDisposable
    {
        private ServerCommands _serverCommands;
        public ServerCommands Server => _serverCommands;
        public class ServerCommands
        {
            private readonly RedisClient _redisClient;

            internal ServerCommands(RedisClient redisClient)
            {
                _redisClient = redisClient;
            }
            
            public ValueTask<string> Info()
            {
                return Info(CancellationToken.None);
            }
        
            public async ValueTask<string> Info(CancellationToken cancellationToken)
            {
                return await  _redisClient.RunWithTimeout(async token =>
                {
                    return await _redisClient.SendAndReceiveAsync(Commands.Info, _redisClient.DataResultProcessor, CancellationToken.None);
                }, cancellationToken);
            }

            public Task<int> DBSize()
            {
                return DBSize(CancellationToken.None);
            }

            public async Task<int> DBSize(CancellationToken cancellationToken)
            {
                return await _redisClient.RunWithTimeout(async token =>
                {
                    return await _redisClient.SendAndReceiveAsync(Commands.DbSize, _redisClient.IntegerResultProcessor, CancellationToken.None);
                }, cancellationToken);
            }
        }
    }
}