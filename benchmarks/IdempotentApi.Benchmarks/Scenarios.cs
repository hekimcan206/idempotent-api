using System.Diagnostics;
using System.Net;
using IdempotentApi.RateLimiting;
using IdempotentApi.Redis;
using Microsoft.AspNetCore.Http;
using StackExchange.Redis;

namespace IdempotentApi.Benchmarks;

internal sealed class Scenarios(string redisConnection, TextWriter log)
{
    private async Task<IConnectionMultiplexer> Connect()
    {
        var options = ConfigurationOptions.Parse(redisConnection);
        options.ConnectTimeout = 15_000;
        options.AsyncTimeout = 15_000;
        return await ConnectionMultiplexer.ConnectAsync(options);
    }

    // ---------------------------------------------------------------------------------------------------
    // 1. Concurrent duplicates: how many times does the handler run when 50 copies of one request arrive at once?
    // ---------------------------------------------------------------------------------------------------
    public async Task<object> DuplicatesAsync(int rounds = 30, int copies = 50, int instances = 2)
    {
        var rows = new List<object>();
        foreach (var impl in new[] { "none", "check-then-set", "lua" })
        {
            var executions = 0;
            var connections = new List<IConnectionMultiplexer>();
            var apps = new List<KestrelInstance>();
            for (var i = 0; i < instances; i++)
            {
                IIdempotencyStore? store = null;
                if (impl != "none")
                {
                    var c = await Connect();
                    connections.Add(c);
                    store = impl == "lua" ? new RedisIdempotencyStore(c) : new CheckThenSetStore(c);
                }

                apps.Add(await KestrelInstance.StartAsync(store, null, async _ =>
                {
                    Interlocked.Increment(ref executions);
                    await Task.Delay(20);
                    return Results.Created("/op/1", new { ok = true });
                }));
            }

            // Warm the connection pools so round 1 does not measure TCP handshakes.
            await Task.WhenAll(apps.Select(a => a.Client.SendAsync(Http.Post(Http.NewKey()))));
            var perRound = new List<int>();
            for (var r = 0; r < rounds; r++)
            {
                var before = Volatile.Read(ref executions);
                var key = Http.NewKey();
                var responses = await Task.WhenAll(Enumerable.Range(0, copies).Select(i => apps[i % instances].Client.SendAsync(Http.Post(impl == "none" ? null : key))));
                foreach (var response in responses)
                {
                    response.Dispose();
                }

                perRound.Add(Volatile.Read(ref executions) - before);
            }

            var row = new
            {
                implementation = impl,
                rounds,
                copiesPerRound = copies,
                instances,
                executionsMedian = Stats.Median(perRound.Select(x => (double)x)),
                executionsMax = perRound.Max(),
                executionsMin = perRound.Min(),
                roundsWithDuplicates = perRound.Count(x => x > 1),
            };
            rows.Add(row);
            log.WriteLine($"duplicates {impl,-15} executions/round median {row.executionsMedian} min {row.executionsMin} max {row.executionsMax}, rounds with >1: {row.roundsWithDuplicates}/{rounds}");

            foreach (var a in apps)
            {
                await a.DisposeAsync();
            }

            foreach (var c in connections)
            {
                await c.DisposeAsync();
            }
        }

        return rows;
    }

    // ---------------------------------------------------------------------------------------------------
    // 2. Wait mode: latency of 50 duplicates of a 100 ms operation, held until the first finishes.
    // ---------------------------------------------------------------------------------------------------
    public async Task<object> WaitModeAsync(int rounds = 10, int copies = 50)
    {
        var executions = 0;
        await using var c1 = await Connect();
        await using var c2 = await Connect();
        await using var a = await KestrelInstance.StartAsync(new RedisIdempotencyStore(c1), p => p.ConcurrencyMode = ConcurrencyMode.WaitForResult, Handler);
        await using var b = await KestrelInstance.StartAsync(new RedisIdempotencyStore(c2), p => p.ConcurrencyMode = ConcurrencyMode.WaitForResult, Handler);
        (await a.Client.SendAsync(Http.Post(Http.NewKey()))).Dispose();
        (await b.Client.SendAsync(Http.Post(Http.NewKey()))).Dispose();
        executions = 0;

        var p50s = new List<double>();
        var maxes = new List<double>();
        var allCreated = true;
        for (var r = 0; r < rounds; r++)
        {
            var key = Http.NewKey();
            var latencies = await Task.WhenAll(Enumerable.Range(0, copies).Select(async i =>
            {
                var start = Stopwatch.GetTimestamp();
                using var response = await (i % 2 == 0 ? a : b).Client.SendAsync(Http.Post(key));
                allCreated &= response.StatusCode == HttpStatusCode.Created;
                return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }));
            var sorted = latencies.Order().ToList();
            p50s.Add(Stats.Percentile(sorted, 50));
            maxes.Add(sorted[^1]);
        }

        var result = new
        {
            rounds,
            copiesPerRound = copies,
            handlerMs = 100,
            executions,
            allResponses201 = allCreated,
            latencyP50MedianMs = Stats.Median(p50s),
            latencyMaxMedianMs = Stats.Median(maxes),
        };
        log.WriteLine($"wait-mode {copies} copies of a 100 ms op: executions {executions} over {rounds} rounds, p50 {Stats.F(result.latencyP50MedianMs)} ms, max {Stats.F(result.latencyMaxMedianMs)} ms (medians of rounds)");
        return result;

        async Task<IResult> Handler(HttpContext _)
        {
            Interlocked.Increment(ref executions);
            await Task.Delay(100);
            return Results.Created("/op/1", new { ok = true });
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // 3. Overhead: latency and throughput of a trivial endpoint with and without the middleware.
    // ---------------------------------------------------------------------------------------------------
    public async Task<object> OverheadAsync(int requests = 5000, int concurrency = 16, int repeats = 3)
    {
        var rows = new List<object>();
        await using var redis = await Connect();
        var configs = new (string Name, IIdempotencyStore? Store, bool Replay)[]
        {
            ("no middleware", null, false),
            ("in-memory store", new InMemoryIdempotencyStore(), false),
            ("redis store, new keys", new RedisIdempotencyStore(redis), false),
            ("redis store, replays", new RedisIdempotencyStore(redis), true),
        };

        foreach (var (name, store, replay) in configs)
        {
            await using var app = await KestrelInstance.StartAsync(store, null, _ => Task.FromResult(Results.Created("/op/1", new { id = 1, amount = "125.50", currency = "TRY" })));
            var replayKey = Http.NewKey();
            Func<int, Task<int>> send = async _ =>
            {
                using var response = await app.Client.SendAsync(Http.Post(store is null ? null : replay ? replayKey : Http.NewKey()));
                return (int)response.StatusCode;
            };

            await Load.RunAsync(1000, concurrency, send);
            var runs = new List<Load.Result>();
            for (var i = 0; i < repeats; i++)
            {
                runs.Add(await Load.RunAsync(requests, concurrency, send));
            }

            var row = new
            {
                configuration = name,
                requests,
                concurrency,
                repeats,
                rpsMedian = Stats.Median(runs.Select(r => r.Rps)),
                p50MsMedian = Stats.Median(runs.Select(r => r.P50)),
                p95MsMedian = Stats.Median(runs.Select(r => r.P95)),
                p99MsMedian = Stats.Median(runs.Select(r => r.P99)),
                statuses = runs[^1].Statuses,
            };
            rows.Add(row);
            log.WriteLine($"overhead {name,-24} {Stats.F(row.rpsMedian, 0),7} req/s  p50 {Stats.F(row.p50MsMedian)} ms  p95 {Stats.F(row.p95MsMedian)} ms  p99 {Stats.F(row.p99MsMedian)} ms  statuses {string.Join(",", row.statuses.Select(s => $"{s.Key}x{s.Value}"))}");
        }

        return rows;
    }

    // ---------------------------------------------------------------------------------------------------
    // 4. Rate limit accuracy: 4 instances x 8 workers hammer one client's bucket.
    // ---------------------------------------------------------------------------------------------------
    public async Task<object> RateLimitAsync(int repeats = 3, double seconds = 3, int instances = 4, int workers = 8)
    {
        var policy = new TokenBucketPolicy("bench", 50, 100);
        var rows = new List<object>();
        var connections = new List<IConnectionMultiplexer>();
        for (var i = 0; i < instances; i++)
        {
            connections.Add(await Connect());
        }

        foreach (var impl in new[] { "lua (shared)", "get-compute-set (shared)", "in-memory (per instance)" })
        {
            var admittedRuns = new List<double>();
            var boundRuns = new List<double>();
            var takesPerSecond = new List<double>();
            for (var run = 0; run < repeats; run++)
            {
                var stores = connections.Select<IConnectionMultiplexer, ITokenBucketStore>(c => impl switch
                {
                    "lua (shared)" => new RedisTokenBucketStore(c),
                    "get-compute-set (shared)" => new GetComputeSetBuckets(c),
                    _ => new InMemoryTokenBucketStore(),
                }).ToList();
                var key = "rl:bench:" + Guid.NewGuid().ToString("N");
                var admitted = 0;
                var takes = 0;
                var stopwatch = Stopwatch.StartNew();
                // Task.Run: the in-memory store completes synchronously, and without it one worker would never yield.
                await Task.WhenAll(stores.SelectMany(s => Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
                {
                    while (stopwatch.Elapsed.TotalSeconds < seconds)
                    {
                        // Yield every take so 32 busy workers share the thread pool fairly (the in-memory store
                        // never awaits on its own).
                        await Task.Yield();
                        var decision = await s.TakeAsync(key, policy, 1, CancellationToken.None);
                        Interlocked.Increment(ref takes);
                        if (decision.Allowed)
                        {
                            Interlocked.Increment(ref admitted);
                        }
                    }
                }))));
                var elapsed = stopwatch.Elapsed.TotalSeconds;
                admittedRuns.Add(admitted);
                boundRuns.Add(policy.Capacity + (policy.RefillPerSecond * elapsed));
                takesPerSecond.Add(takes / elapsed);
            }

            var admittedMedian = Stats.Median(admittedRuns);
            var boundMedian = Stats.Median(boundRuns);
            var row = new
            {
                implementation = impl,
                instances,
                workersPerInstance = workers,
                seconds,
                capacity = policy.Capacity,
                refillPerSecond = policy.RefillPerSecond,
                repeats,
                admittedMedian,
                boundMedian,
                admittedOverBound = admittedMedian / boundMedian,
                admittedRuns,
                takesPerSecondMedian = Stats.Median(takesPerSecond),
            };
            rows.Add(row);
            log.WriteLine($"ratelimit {impl,-26} admitted {admittedMedian} vs bound {Stats.F(boundMedian, 0)} ({Stats.F(row.admittedOverBound * 100, 1)}%), {Stats.F(row.takesPerSecondMedian, 0)} decisions/s; runs [{string.Join(", ", admittedRuns)}]");
        }

        foreach (var c in connections)
        {
            await c.DisposeAsync();
        }

        return rows;
    }

    // ---------------------------------------------------------------------------------------------------
    // 5. Store round trips: latency of one script call from one connection, sequentially.
    // ---------------------------------------------------------------------------------------------------
    public async Task<object> StoreLatencyAsync(int operations = 5000)
    {
        await using var redis = await Connect();
        var store = new RedisIdempotencyStore(redis);
        var buckets = new RedisTokenBucketStore(redis);
        var policy = new TokenBucketPolicy("lat", 1_000_000, 1_000_000);
        var body = new StoredResponse(201, [new("Content-Type", "application/json"), new("Location", "/payments/1")], new byte[512]);

        async Task<double[]> Measure(Func<int, Task> op)
        {
            for (var i = 0; i < 500; i++)
            {
                await op(i);
            }

            var samples = new double[operations];
            for (var i = 0; i < operations; i++)
            {
                var start = Stopwatch.GetTimestamp();
                await op(500 + i);
                samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }

            Array.Sort(samples);
            return samples;
        }

        var keys = Enumerable.Range(0, operations + 500).Select(_ => "lat:" + Guid.NewGuid().ToString("N")).ToArray();
        var leases = new IdempotencyLease[keys.Length];
        var begin = await Measure(async i => leases[i] = (await store.TryBeginAsync(keys[i], "fp", TimeSpan.FromMinutes(1), CancellationToken.None)).Lease!);
        var complete = await Measure(async i => await store.CompleteAsync(keys[i], leases[i].OwnerToken, body, TimeSpan.FromMinutes(5), CancellationToken.None));
        var replay = await Measure(async i => await store.TryBeginAsync(keys[i], "fp", TimeSpan.FromMinutes(1), CancellationToken.None));
        var take = await Measure(async _ => await buckets.TakeAsync("lat:bucket", policy, 1, CancellationToken.None));
        var ping = await Measure(async _ => await redis.GetDatabase().PingAsync());

        StoreRow Row(string op, double[] s) => new(op, Stats.Percentile(s, 50), Stats.Percentile(s, 99));
        var rows = new[]
        {
            Row("PING (baseline round trip)", ping),
            Row("begin.lua (claim)", begin),
            Row("complete.lua (store 512 B response)", complete),
            Row("begin.lua (completed -> replay)", replay),
            Row("token_bucket.lua (take)", take),
        };
        foreach (var r in rows)
        {
            log.WriteLine($"store {r.Operation,-38} p50 {Stats.F(r.P50Ms, 3)} ms  p99 {Stats.F(r.P99Ms, 3)} ms");
        }

        return rows;
    }
}

internal sealed record StoreRow(string Operation, double P50Ms, double P99Ms);
