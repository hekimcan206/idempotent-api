# 0005. A distributed token bucket in Lua on the Redis clock, with IETF RateLimit headers

- Status: accepted
- Date: 2026-11-12

## Context and problem statement

A per-client rate limit only means something when it holds across all instances. With per-instance limits, four
instances admit four times the policy. We also want clients to see their remaining quota, and the IETF HTTPAPI
draft for `RateLimit` and `RateLimit-Policy` (revision 11, May 2026) standardises how.

## Considered options

Algorithm and placement:

1. In-memory buckets per instance: `System.Threading.RateLimiting.TokenBucketRateLimiter`, or our in-memory store.
2. Shared state in Redis, computed in the application: `HMGET` the bucket, run the arithmetic in C#, `HSET` it back.
3. Shared state in Redis, computed in a Lua script that also takes the time from Redis (`TIME`).
4. Fixed or sliding window counters (`INCR` plus `EXPIRE`).

Integration:

- a. An adapter that exposes the bucket as a `System.Threading.RateLimiting.RateLimiter` for ASP.NET Core's built-in
  middleware.
- b. A small dedicated middleware.

## Decision outcome

Chosen options: **3 and b.**

- **Token bucket** rather than windows. A burst up to `capacity` is allowed, then the bucket refills at a steady
  rate. Over any interval of length T it admits at most `capacity + rate × T`. Fixed windows allow a 2× burst at the
  boundary between two windows.
- **Lua** makes read-refill-decide-write one atomic step. Measured: 4 instances × 8 workers hammered one bucket
  (capacity 50, 100 tokens/s) for 3 s. The bound was 350, and the shared bucket admitted:

  | store | admitted (median of 3) | vs bound |
  |---|---|---|
  | Lua, shared | 349 | 99.7 % |
  | get-compute-set, shared (option 2) | 6,086 | 1,738 % |
  | in-memory, per instance (option 1) | 1,400 | 400 % |

  Option 2 does not drift a little. It overruns by 17×, because every concurrent reader sees the same "tokens left"
  and spends it. Option 1 is exact per instance, which makes it 4× too generous for the deployment.
- **Redis server time.** Each instance's own clock would make refill depend on host clock skew. With `TIME` inside
  the script, every instance shares one timeline. Redis 7 replicates script *effects* by default, so calling `TIME`
  before a write is allowed. If the clock steps backwards (for example after a failover to a replica whose clock is
  behind), the script refills nothing until time catches up, instead of minting tokens. A property test covers this.
- The C# `TokenBucketMath` performs the same arithmetic and backs the in-memory store. FsCheck properties state the
  guarantees on it: never more than `capacity + rate × T` over any interval; tokens stay within `[0, capacity]`; a
  request retried exactly at `Retry-After` succeeds. A parity test checks the Lua script against the C# arithmetic
  on Redis.
- A floating-point refill can land a hair below the cost (0.99999999 tokens). The comparison therefore uses a 1e-9
  tolerance. The property test that found this is `A_denied_request_succeeds_exactly_when_retry_after_says`.

On integration, we chose **b** because the built-in rate limiting middleware asks the limiter for a lease and never
reports the remaining quota per request. The `RateLimit` header needs exactly that number. The dedicated middleware:

- emits `RateLimit-Policy: "name";q=capacity;w=seconds-to-refill-from-empty`;
- emits `RateLimit: "name";r=remaining;t=seconds-until-full`;
- on denial, answers 429 with `Retry-After` and a `quota-exceeded` problem document that lists `violated-policies`,
  following the draft's example.

For a token bucket, `t` is the time until the bucket is full again. A client that spends at most `r` before then
cannot be throttled by this policy, which matches the draft's definition of the effective window.

**Fail open on store outage, by default.** A limiter protects capacity. Refusing all traffic because the limiter
cannot reach Redis turns a partial outage into a full one. `FailOpen = false` is available for limits that protect
money, not capacity.

### Consequences

- Good: one limit for the whole deployment, within 0.3 % of the theoretical bound under heavy contention.
- Good: clients can pace themselves from the headers.
- Bad: one Redis round trip per limited request, 0.95 ms p50 here. Hot endpoints pay it on every call.
- Bad: when Redis is down, the limit is not enforced (fail-open), and a metric and an error log record this.
- Bad: this is not a `RateLimiter`, so it does not compose with `[EnableRateLimiting]` attributes.

## Links

- draft-ietf-httpapi-ratelimit-headers-11: https://www.ietf.org/archive/id/draft-ietf-httpapi-ratelimit-headers-11.txt
- Datatracker page: https://datatracker.ietf.org/doc/draft-ietf-httpapi-ratelimit-headers/
- Redis token bucket guide (Lua with `TIME` from the server): https://redis.io/docs/latest/develop/use-cases/rate-limiter/
- Measured comparison: `benchmark-results/ratelimit.json`
