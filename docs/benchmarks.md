# Benchmarks

All numbers below were measured on the machine described here. Nothing is estimated. The raw output of every run is
committed in [`benchmark-results/`](../benchmark-results).

## Environment

| | |
|---|---|
| CPU | 13th Gen Intel Core i7-13620H, 10 cores / 16 threads |
| RAM | 15.7 GB |
| OS | Windows 11 (10.0.26200), x64 |
| .NET | SDK 10.0.401, runtime 10.0.12, Release build |
| Redis | `redis:7-alpine` in Docker Desktop 29.9 (WSL2 VM: 16 vCPU, 7.6 GB), started by Testcontainers, reached over loopback TCP |
| API | Real Kestrel instances on `127.0.0.1` with `SocketsHttpHandler` clients in the same process |
| Host load | Shared workstation. Windows reported about 60 % CPU load before the runs, and 11 unrelated containers were running, including another agent's load-test lab. Treat absolute numbers as conservative and compare rows within a table, not across machines. |

How to reproduce (Docker must be running; Redis is started automatically):

```bash
dotnet run -c Release --project benchmarks/IdempotentApi.Benchmarks -- all          # or: duplicates | wait | overhead | ratelimit | store
dotnet run -c Release --project benchmarks/IdempotentApi.Benchmarks -- micro --filter '*' --job short
```

## 1. Concurrent duplicates: how often does the handler run?

Two API instances, each with its own Redis connection. In each of 30 rounds, 50 copies of one request (same key, same
body) are sent at once, alternating between the instances. The handler takes 20 ms, which keeps the race window
realistic. "Executions" counts real handler invocations.

| Store | Executions per round (median) | min | max | Rounds with a duplicate |
|---|---:|---:|---:|---:|
| No idempotency middleware | 50 | 50 | 50 | 30 / 30 |
| Check-then-set on Redis (`HGETALL`, then `HSET` + `EXPIRE`) | 9 | 4 | 15 | 30 / 30 |
| **IdempotentApi.Redis (Lua scripts)** | **1** | **1** | **1** | **0 / 30** |

The naive store does not fail occasionally. It fails in every round, because all 50 requests read "absent" before
any of them writes. Source: `benchmark-results/duplicates.json`. Implementation of the naive store:
`benchmarks/IdempotentApi.Benchmarks/NaiveStores.cs`.

## 2. Wait mode: cost of holding duplicates

50 concurrent copies of a 100 ms operation over two instances, in wait mode (`ConcurrencyMode.WaitForResult`), for
10 rounds.

| Metric | Value |
|---|---:|
| Handler executions | 10 in 10 rounds (1 per round) |
| Responses | all 500 were 201, with an identical body within each round |
| Latency p50 (median of rounds) | 128.5 ms |
| Latency max (median of rounds) | 228.4 ms |

The operation itself takes 100 ms. The difference is the polling backoff (5 ms doubling to a 100 ms cap, with jitter;
see ADR 0003). Source: `benchmark-results/wait.json`.

## 3. Store round trips

Sequential calls from one connection, 5,000 measured after 500 warm-up calls.

| Operation | p50 | p99 |
|---|---:|---:|
| `PING` (round-trip baseline) | 0.850 ms | 3.474 ms |
| `begin.lua`: claim a new key | 0.956 ms | 5.982 ms |
| `complete.lua`: store a 512 B response | 0.874 ms | 8.663 ms |
| `begin.lua`: completed key, returns the response for replay | 0.880 ms | 4.748 ms |
| `token_bucket.lua`: one take | 0.946 ms | 6.392 ms |

Every script costs about one network round trip. The script work itself is a small fraction of it. The p99 values
reflect the shared host. Source: `benchmark-results/store.json`.

## 4. End-to-end overhead

A trivial `POST` endpoint that returns 201 with a small JSON body. 16 concurrent closed-loop clients, 5,000 requests
per run after 1,000 warm-up requests, 3 runs; the table shows the median of the 3 runs. Each "new keys" request
uses a fresh key (claim, execute, complete). "Replays" sends one key repeatedly (claim attempt, then replay).

After the renewal fix (see below), two separate runs:

| Configuration | Run 1 req/s | p50 | p95 | p99 | Run 2 req/s | p50 | p95 | p99 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| No middleware | 52,466 | 0.26 ms | 0.44 ms | 0.85 ms | 40,608 | 0.32 ms | 0.52 ms | 1.68 ms |
| In-memory store | 29,347 | 0.36 ms | 1.25 ms | 4.04 ms | 24,140 | 0.38 ms | 2.03 ms | 5.54 ms |
| Redis store, new keys | 6,136 | 2.42 ms | 3.81 ms | 5.32 ms | 4,531 | 3.08 ms | 6.68 ms | 10.52 ms |
| Redis store, replays | 9,661 | 1.41 ms | 3.27 ms | 4.65 ms | 5,213 | 2.06 ms | 7.90 ms | 12.76 ms |

How to read this:

- A new key costs two Redis round trips: `begin` before the handler and `complete` after it. A replay costs one. On
  this host that is roughly 2-3 ms and 1.4-2 ms p50 respectively, almost all of it Redis I/O through Docker Desktop's
  VM. On a Linux host with Redis on the same network, round trips are typically far shorter. That was not measured here.
- The in-memory store shows the pure CPU cost of the middleware: buffering, fingerprinting, response capture and the
  renewal timer. Throughput was 56-59 % of the bare endpoint, with p50 rising by about 0.1 ms.
- The run-to-run spread (for example 40.6k vs 52.5k req/s with no middleware) is host noise. Compare rows within
  one run.

### Before/after: lease renewal without an exception per request

The first version stopped the renewal loop by cancelling a `CancellationToken`. That threw, and caught, an
`OperationCanceledException` on every executed request. The fix disposes the `PeriodicTimer` instead, which ends
`WaitForNextTickAsync` with `false`. The before and after runs were interleaved (before, after, before, after) on the
same host.

| | In-memory store req/s | No-middleware req/s (same run) | Ratio |
|---|---:|---:|---:|
| Before, run 1 | 17,709 | 45,573 | 39 % |
| Before, run 2 | 20,595 | 44,275 | 47 % |
| After, run 1 | 29,347 | 52,466 | 56 % |
| After, run 2 | 24,140 | 40,608 | 59 % |

The Redis rows moved within run-to-run noise (new keys 4,778 / 3,541 req/s before, 6,136 / 4,531 after), so no
improvement is claimed for them. Sources: `overhead-before-renewal-fix*.json`, `overhead.json` and `overhead-run2.json`.

## 5. Rate-limit accuracy under contention

One client's bucket (capacity 50, refill 100 tokens/s) is hammered for 3 s by 4 "instances" × 8 workers. Each
instance has its own Redis connection, or its own memory for the in-memory row. The theoretical maximum is
`capacity + rate × elapsed` ≈ 350. Median of 3 runs.

| Store | Admitted (runs) | Bound | Admitted / bound | Decisions/s |
|---|---|---:|---:|---:|
| **Lua token bucket, shared** | **349** (348, 349, 349) | 350 | **99.7 %** | 23,064 |
| Get-compute-set in C#, shared (same arithmetic, no atomicity) | 6,086 (6,086, 6,064, 6,177) | 350 | 1,738 % | 15,556 |
| In-memory, one bucket per instance | 1,400 (1,400 ×3) | 350 | 400 % | 6.0 M |

The Lua bucket stays within the bound, and close to it. Splitting read and write lets concurrent workers spend the
same tokens, a 17× overrun. Per-instance memory is exact per instance, which is exactly 4× too much for 4 instances.
Source: `benchmark-results/ratelimit.json`.

## 6. Micro-benchmarks (BenchmarkDotNet, ShortRun)

| Method | Mean | Allocated |
|---|---:|---:|
| Parse an sf-string key (36 chars) | 79.9 ns | 248 B |
| Storage key (SHA-256 of partition + key) | 335.6 ns | 624 B |
| Fingerprint, 1 KiB body | 1.14 µs | 400 B |
| Fingerprint, 64 KiB body | 32.3 µs | 400 B |
| Token bucket arithmetic (one take) | 5.9 ns | 0 B |

ShortRun uses 3 iterations, so the error columns in `benchmark-results/micro.md` are wide on this loaded host. The
order of magnitude is the useful part: the per-request CPU work is microseconds, against a Redis round trip of about
a millisecond.

## 7. docker compose demo

`docker compose up` with prebuilt images: Redis, two API instances and the driver. The driver exited 0 about 7 s
after `up` was issued, including container start, readiness waits and all six scenarios. The six-scenario run itself
takes well under a second; the 160-request burst completed in 0.13 s. The image build from a cold cache took 49 s.

## Not measured

- Redis on a separate host or a managed service, and Redis Cluster or Sentinel failover. Everything ran against one
  local container.
- Linux host numbers. All runs were on Windows with Redis inside Docker Desktop's VM, which inflates round trips.
- Long-duration soak tests (memory growth over hours).
