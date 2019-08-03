using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using TomLonghurst.RedisClient.Models;
using TomLonghurst.RedisClient.Models.Backlog;

namespace TomLonghurst.RedisClient.Client
{
    public partial class RedisClient
    {
        private WeakReference<RedisClient> _weakReference;

        private readonly ConcurrentQueue<IBacklog> _backlog = new ConcurrentQueue<IBacklog>();
        
        private static readonly Action<object> _processBacklogAction = s =>
        {
            var wr = (WeakReference<RedisClient>)s;
            if (wr.TryGetTarget(out var redisClient))
            {
                redisClient.ProcessBacklog();   
            }
        };

        private void StartBacklogProcessor()
        {
            _pipeScheduler.Schedule(_processBacklogAction, _weakReference);
        }

        private void ProcessBacklog()
        {
            if (_backlog.Count > 0)
            {
              var backlogRedisClient = ConnectAsync(ClientConfig);  
                while (_backlog.Count > 0)
                {
                    if (_backlog.TryDequeue(out var backlogItem))
                    {
                        backlogRedisClient.WriteAndReceiveBacklog(backlogItem);
                    }
                }
            }
        }

        private async Task WriteAndReceiveBacklog(IBacklog backlogItem)
        {
            await Write(backlogItem.RedisCommand);
            
        }
    }
}