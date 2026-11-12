using System.Diagnostics;
using System.Net;
using IdempotentApi.IntegrationTests.Infrastructure;
using IdempotentApi.RateLimiting;
using IdempotentApi.Redis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace IdempotentApi.IntegrationTests;

public sealed class RedisTokenBucketTests(RedisFixture redis)
{
    private static string Bucket() => "rl:test:" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Burst_is_exactly_the_capacity()
    {
        var store = new RedisTokenBucketStore(redis.Redis);
        var policy = new TokenBucketPolicy("burst", 10, 0.01);
        var key = Bucket();

        var decisions = new List<TokenBucketDecision>();
        for (var i = 0; i < 12; i++)
        {
            decisions.Add(await store.TakeAsync(key, policy, 1, CancellationToken.None));
        }

        decisions.Take(10).Should().OnlyContain(d => d.Allowed);
        decisions.Take(10).Select(d => d.Remaining).Should().Equal(9, 8, 7, 6, 5, 4, 3, 2, 1, 0);
        decisions.Skip(10).Should().OnlyContain(d => !d.Allowed);
        decisions[10].RetryAfter.Should().BeCloseTo(TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Four_instances_hammering_one_bucket_stay_within_the_bound()
    {
        // capacity 20, 50 tokens/s, 2 s of unthrottled demand from 4 connections x 8 workers.
        var policy = new TokenBucketPolicy("shared", 20, 50);
        var key = Bucket();
        var connections = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => redis.Connect()));
        try
        {
            var admitted = 0;
            var stopwatch = Stopwatch.StartNew();
            var duration = TimeSpan.FromSeconds(2);
            await Task.WhenAll(connections.SelectMany(c => Enumerable.Range(0, 8).Select(async _ =>
            {
                var store = new RedisTokenBucketStore(c);
                while (stopwatch.Elapsed < duration)
                {
                    if ((await store.TakeAsync(key, policy, 1, CancellationToken.None)).Allowed)
                    {
                        Interlocked.Increment(ref admitted);
                    }
                }
            })));
            var elapsed = stopwatch.Elapsed.TotalSeconds;

            var bound = policy.Capacity + (policy.RefillPerSecond * elapsed);
            admitted.Should().BeLessThanOrEqualTo((int)Math.Floor(bound), "a shared bucket never admits more than capacity + rate * T");
            admitted.Should().BeGreaterThanOrEqualTo((int)(bound * 0.9), "and under continuous demand it admits close to that");
        }
        finally
        {
            foreach (var c in connections)
            {
                await c.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Retry_after_is_honest()
    {
        var store = new RedisTokenBucketStore(redis.Redis);
        var policy = new TokenBucketPolicy("p", 1, 4); // one token every 250 ms
        var key = Bucket();

        (await store.TakeAsync(key, policy, 1, CancellationToken.None)).Allowed.Should().BeTrue();
        var denied = await store.TakeAsync(key, policy, 1, CancellationToken.None);
        denied.Allowed.Should().BeFalse();
        denied.RetryAfter.Should().BeGreaterThan(TimeSpan.FromMilliseconds(200)).And.BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(250));

        await Task.Delay(denied.RetryAfter + TimeSpan.FromMilliseconds(20));
        (await store.TakeAsync(key, policy, 1, CancellationToken.None)).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Bucket_expires_once_it_would_be_full_again()
    {
        var store = new RedisTokenBucketStore(redis.Redis);
        var key = Bucket();
        await store.TakeAsync(key, new TokenBucketPolicy("p", 10, 5), 4, CancellationToken.None);

        var ttl = await redis.Redis.GetDatabase().KeyTimeToLiveAsync(key);

        // 4 tokens at 5/s refill in 0.8 s, plus a 1 s margin.
        ttl.Should().BeCloseTo(TimeSpan.FromMilliseconds(1800), TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task Rate_limit_holds_across_two_instances_and_emits_headers()
    {
        var prefix = "rl:" + Guid.NewGuid().ToString("N");
        void Policies(TokenBucketRateLimitOptions o)
        {
            o.KeyPrefix = prefix;
            o.AddPolicy("orders", capacity: 6, refillPerSecond: 0.1);
            o.PartitionResolver = c => c.Request.Headers["X-Client"].ToString();
        }

        await using var a = await AppInstance.StartAsync(redis, app => app.MapPost("/orders", () => Results.Ok()).RequireTokenBucket("orders"), rateLimits: Policies);
        await using var b = await AppInstance.StartAsync(redis, app => app.MapPost("/orders", () => Results.Ok()).RequireTokenBucket("orders"), rateLimits: Policies);

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 10; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/orders");
            request.Headers.Add("X-Client", "client-1");
            last?.Dispose();
            last = await (i % 2 == 0 ? a : b).Client.SendAsync(request);
            statuses.Add(last.StatusCode);
        }

        statuses.Count(s => s == HttpStatusCode.OK).Should().Be(6);
        statuses.Skip(6).Should().OnlyContain(s => s == HttpStatusCode.TooManyRequests);
        last!.Headers.GetValues("RateLimit").Single().Should().StartWith("\"orders\";r=0;t=");
        last.Headers.GetValues("RateLimit-Policy").Single().Should().Be("\"orders\";q=6;w=60");
        last.Headers.RetryAfter!.Delta!.Value.TotalSeconds.Should().BeInRange(1, 10);
        last.Dispose();
    }

    [Fact]
    public async Task Lua_and_csharp_agree_on_a_burst_sequence()
    {
        var store = new RedisTokenBucketStore(redis.Redis);
        var policy = new TokenBucketPolicy("p", 7, 0.001);
        var key = Bucket();
        TokenBucketState? state = null;
        var now = 0L;

        foreach (var cost in new[] { 1, 3, 2, 2, 1, 1, 7 })
        {
            var lua = await store.TakeAsync(key, policy, cost, CancellationToken.None);
            var (next, csharp) = TokenBucketMath.Take(state, now, policy, cost);
            state = next;

            lua.Allowed.Should().Be(csharp.Allowed, $"cost {cost}");
            lua.Remaining.Should().Be(csharp.Remaining, $"cost {cost}");
        }
    }
}
