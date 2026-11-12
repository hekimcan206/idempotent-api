# Threat model

Scope: the `IdempotentApi.AspNetCore` and `IdempotentApi.Redis` packages, used in an ASP.NET Core API that runs as
several instances and shares one Redis. The sample payments API is in scope only as a reference deployment. The
model is STRIDE-style: what we protect, who attacks it, how, and what the code does about it.

## Assets

| Asset | Why it matters |
|---|---|
| **Single execution of an operation** | The whole point. A broken guarantee means a double charge, a double shipment or a double transfer. |
| **Stored responses** | Replayed for 24 h. They can contain personal or payment data from the original response. |
| **Rate-limit budgets** | They protect capacity and the downstream services. |
| **Redis** | It holds all of the above. Write access to it means control of all of the above. |

## Actors

- **Legitimate client with bad retry behaviour**: aggressive retries, hedged requests, retries with a changed body.
  Not malicious, but by far the most common source of duplicates.
- **Malicious API client** (authenticated or not): tries to read other clients' responses, exhaust quota, poison
  keys, or make the server do expensive work.
- **Network attacker**: sees or alters traffic between the API and Redis.
- **Infrastructure failure**: an instance crash, a GC pause, a network partition, a Redis failover.

## Threats and mitigations

| # | STRIDE | Threat | Mitigation | Residual risk |
|---|---|---|---|---|
| T1 | Tampering | **Race:** concurrent copies of one request all execute | Claim is one atomic Lua script. Owner token on complete, renew and release. Tested with 4 instances × 100 copies and 8 connections × 25 claims. Measured 1 execution in 30/30 rounds (a check-then-set store: 4-15 executions) | None while Redis is consistent (see T7) |
| T2 | Information disclosure | **Cross-client replay:** client B sends client A's key and receives A's stored response | Keys are scoped by a partition (authenticated user, or the API's client id) and hashed together with it. Minimum key length 16. Test `Keys_are_scoped_to_the_caller` | Anonymous endpoints share the `anonymous` partition. The documentation says to configure a partition resolver for any endpoint whose responses are not public |
| T3 | Information disclosure | **Guessable keys:** low-entropy keys allow T2 within a partition, for example a shared service account | Minimum length 16. The documentation and 400 messages recommend UUIDs | A client that uses `aaaaaaaaaaaaaaaa` defeats the check. The server cannot measure entropy |
| T4 | Tampering | **Key reuse with a different body**, to change a committed operation's parameters or confuse the server | SHA-256 fingerprint over method, path, query, media type and body. Mismatch gives 422, and nothing executes. The storage key ignores the path, so reuse on another endpoint also gives 422 | None |
| T5 | Tampering / Injection | **Key injection** into Redis key names (`*`, `\r\n`, very long keys) | The key is parsed as a strict sf-string, at most 255 characters. Redis only ever sees `prefix:<sha256 hex>`. Header values with CR/LF are dropped from stored headers | None |
| T6 | Spoofing / Information disclosure | **Replay leaks session state:** a replay carries the original `Set-Cookie` | Denylist: `Set-Cookie`, `Date`, rate-limit headers and trace headers are never stored. Test `Cookies_are_never_replayed` | Custom headers that carry per-caller secrets would be replayed. Keep them out of idempotent responses, or extend the denylist |
| T7 | Tampering | **Redis failover loses a fresh claim** (asynchronous replication), so a retry executes on the new primary | Documented limitation. `WAIT 1` after the claim would narrow the window, at the cost of latency. Fencing tokens let the database reject the older writer | A duplicate is possible during a failover window. This is the main residual risk; see README Limitations |
| T8 | Tampering | **Paused owner:** a GC pause or partition lets the lease lapse, a second instance executes, and the first then wakes up and writes | Leases renew every lease/3. A failed renewal cancels `IIdempotencyFeature.LeaseLost`. The late `complete` is refused by owner-token check. A monotonic fencing token is handed to the handler for database-level fencing | A handler that ignores both `LeaseLost` and the fencing token can still double-apply after a pause longer than the lease (30 s by default) |
| T9 | Denial of service | **Store outage makes the API unsafe** | Idempotency fails closed (503 with `Retry-After`, no execution). Readiness reports unhealthy without Redis, so load balancers drain the instance | Idempotent endpoints are unavailable while Redis is down. This is a deliberate choice |
| T10 | Denial of service | **Large bodies** used to exhaust memory through buffering | Request body capped at 1 MiB (413 before buffering beyond the limit). Stored response capped at 1 MiB | Concurrency × 1 MiB of buffers. Combine with Kestrel's request limits |
| T11 | Denial of service | **Key flooding:** many unique keys fill Redis | Every record has a TTL (lease, then 24 h retention). The rate limiter runs before idempotency, so each new key spends quota | Memory ≈ requests/day × response size. Size Redis and `maxmemory-policy` accordingly (`noeviction` is the safe choice; eviction would forget completed keys) |
| T12 | Denial of service | **Quota exhaustion** of one client by another | Buckets are partitioned per client. The default is user, else IP; the sample uses the client id | Behind a proxy, the IP partition collapses to the proxy unless forwarded headers are configured (documented) |
| T13 | Denial of service | **Limiter bypass while Redis is down** (fail-open) | Default fail-open, logged and counted (`ratelimit.decisions{result=fail_open}`). `FailOpen = false` is available | No limit while Redis is down |
| T14 | Repudiation | **No trace of what was replayed** | `Idempotent-Replayed: true` on replays. The `idempotency.requests{outcome}` metric. Lease-lost and store errors logged with the storage key (a hash, not the client's key) | None |
| T15 | Information disclosure | **Stored responses at rest in Redis**, and **traffic to Redis in clear text** | Responses expire after 24 h. Use Redis ACLs and TLS (`ssl=true` in the connection string) in production. The sample runs on a private compose network | Stored bodies are not encrypted by the library. Keep sensitive fields out of idempotent responses, or wrap the store with encryption |

## Out of scope

- Authenticating callers. The partition resolver trusts whatever identity the application established before the
  middleware.
- Exactly-once side effects in third-party systems. The middleware guarantees at most one *execution of the handler*
  per key. A handler that calls a payment provider should also pass the key to the provider.
