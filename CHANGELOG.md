# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-11-12

### Added

- `IdempotentApi.AspNetCore` package: middleware for draft-ietf-httpapi-idempotency-key-header-07 on endpoints marked
  `RequireIdempotency()`. It includes a strict RFC 9651 sf-string key parser (bare keys opt-in), a SHA-256 request
  fingerprint (method, path, query, media type, body), caller-scoped hashed storage keys, response capture while
  streaming, and replay with `Idempotent-Replayed: true`.
- Status semantics from the draft: 400 for a missing or invalid key, 422 for a key reused with another fingerprint,
  409 for a request in flight. All are RFC 9457 problem documents linking to the published idempotency specification.
- Wait-and-replay mode (`ConcurrencyMode.WaitForResult`) with capped exponential backoff. A waiter executes if the
  original fails.
- Lease renewal while the handler runs, and `IIdempotencyFeature` with the key, a monotonic fencing token and a
  `LeaseLost` token.
- Outcome policy: 2xx/4xx are stored (except 408/409/425/429); 5xx and exceptions release the key; oversized
  responses are recorded as executed but not replayable; a store outage gives 503 and nothing runs.
- Token-bucket rate limiting with draft-ietf-httpapi-ratelimit-headers-11 `RateLimit` / `RateLimit-Policy` fields, 429
  with `Retry-After` and a `quota-exceeded` problem document, per-client partitions and fail-open/closed handling.
- `IdempotentApi.Redis` package: claim/renew/complete/release as atomic Lua scripts with owner tokens, a fencing
  counter and TTLs that follow the lease, then the retention. A token bucket script on the Redis server clock.
- In-memory stores for single-instance use and tests; OpenTelemetry meter and activity source (`IdempotentApi`).
- Sample payments API (Serilog JSON logs, OpenTelemetry, health checks, Scalar) and a `docker compose up` demo: two
  instances, one Redis, and a driver that asserts six scenarios.
- Tests: unit, FsCheck property tests (token bucket bounds, sf-string round trip), golden error bodies, a store
  contract shared by the in-memory and Redis stores, and Testcontainers multi-instance tests (single execution, crash
  takeover, outage, shared limit).
- Benchmark harness and BenchmarkDotNet micro-benchmarks with committed results; CI (build, tests with coverage,
  vulnerable-package audit, package pack and consume, container image, compose demo) and CodeQL.

### Performance

- Lease renewal stops by disposing its timer instead of cancelling a token, which removes one thrown
  `OperationCanceledException` per executed request. In-memory store throughput rose from 39-47 % to 56-59 % of the
  no-middleware baseline (see `docs/benchmarks.md`).

[1.0.0]: https://github.com/hekimcan206/idempotent-api/releases/tag/v1.0.0
