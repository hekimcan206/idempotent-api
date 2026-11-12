# 0003. Reject concurrent duplicates with 409 by default; wait-and-replay as an opt-in mode

- Status: accepted
- Date: 2026-11-12

## Context and problem statement

When a duplicate arrives while the original is still running, the server has to answer it somehow. The draft
specifies 409 Conflict, after which the client retries later. Some clients, however, fire duplicates by design
(hedged requests, or retry-on-timeout with a short timeout). For them, a 409 only turns into another round trip,
when the useful answer, the original's result, is a few milliseconds away.

## Considered options

1. Always 409 (the draft).
2. Always wait for the original and replay it.
3. 409 by default; wait-and-replay per endpoint (`ConcurrencyMode.WaitForResult`).

For waiting, two ways to learn that the original finished:

- a. Poll the store with capped exponential backoff and jitter.
- b. Redis pub/sub or keyspace notifications.

## Decision outcome

Chosen option: **3 with polling (a).**

The default follows the draft, so a client that implements the draft sees the behaviour the draft describes. Wait
mode is a per-endpoint choice. It is still a single execution: the waiter polls the same atomic `begin` script, and
receives either `Completed` (replay), `Acquired` (the original failed and released the key, so the waiter executes
instead), or `InFlight` (keep waiting). After `WaitTimeout` (10 s by default) the waiter gives up with the same 409 as
reject mode, so a stuck original cannot hold connections indefinitely.

Polling starts at 5 ms, doubles to a 100 ms cap, and adds up to 50 % jitter. We chose it over pub/sub because:

- Pub/sub needs a subscription per waiting request, or a multiplexing layer, and keyspace notifications must be
  enabled on the server (`notify-keyspace-events`), which managed Redis offerings do not always allow.
- Polling also covers the crashed-owner case for free. When the lease lapses, the next poll claims the key. A
  notification-based design would need a timeout path for this anyway.
- Measured cost: 50 duplicates of a 100 ms operation across two instances finished with a median latency of 128.5 ms
  and a median worst case of 228.4 ms, with exactly one execution in each of 10 rounds (`docs/benchmarks.md`).
  Polling adds roughly 30 ms at the median.

### Consequences

- Good: draft-compliant by default. Clients that hedge get one answer instead of a 409 storm.
- Good: a failed original does not strand its waiters; one of them executes.
- Bad: each waiting request polls Redis (at most about 10 polls per second after the backoff). Wait mode suits
  endpoints with short handlers, not long-running jobs. For those, use reject mode and an asynchronous status
  resource.
- Bad: a waiter holds a server connection and a request slot while it waits.

## Links

- draft-ietf-httpapi-idempotency-key-header-07, section on concurrent requests (409): https://datatracker.ietf.org/doc/html/draft-ietf-httpapi-idempotency-key-header-07
- Redis keyspace notifications (configuration requirement): https://redis.io/docs/latest/develop/pubsub/keyspace-notifications/
