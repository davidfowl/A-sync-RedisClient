﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using RedisClientTest;
using StackExchange.Profiling;
using TomLonghurst.RedisClient.Client;
using TomLonghurst.RedisClient.Enums;

namespace TestConsoleApp
{
    class Program
    {
        private static RedisClientManager _redisManager;
        private static Task<RedisClient> TomLonghurstRedisClient => _redisManager.GetRedisClient();

        static async Task Main(string[] args)
        {

            var config = new RedisClientConfig("localhost", 6379);


            _redisManager = new RedisClientManager(config, 1);

            await (await TomLonghurstRedisClient).StringSetAsync("SingleKey",
                "123",
                120,
                AwaitOptions.AwaitCompletion);

            var tasks = new List<Task>();

            for (var taskCount = 0; taskCount < 1; taskCount++)
            {
                var task = Task.Run(async () =>
                {
                    var tomLonghurstRedisClientStopwatch = Stopwatch.StartNew();

                    for (var i = 0; i < 1; i++)
                    {
                        var redisValue = await (await TomLonghurstRedisClient).StringGetAsync("SingleKey");
                    }

                    tomLonghurstRedisClientStopwatch.Stop();
                    var tomLonghurstRedisClientStopwatchTimeTaken =
                        tomLonghurstRedisClientStopwatch.ElapsedMilliseconds;
                    Console.WriteLine($"Time Taken: {tomLonghurstRedisClientStopwatchTimeTaken} ms");
                });

                tasks.Add(task);
            }

            await Task.WhenAll(tasks);
        }
    }
}