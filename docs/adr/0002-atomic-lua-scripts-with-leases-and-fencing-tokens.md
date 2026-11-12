# 0002. Single execution through atomic Redis Lua scripts with leases, owner tokens and fencing tokens

- Status: accepted
- Date: 2026-11-12

## Context and problem statement

The hard part of idempotency is the concurrent case: 50 copies of one request arriving at several instances at the
same moment, for example from an aggressive client retry policy or a load balancer that retries. Each instance asks
the store "has this key run?". If two of them can both hear "no", the operation runs twice, and the key has failed at
its one job. The store must turn "check, then claim" into a single indivisible step that holds across instances.

## Decision drivers

- Exactly one execution per key while the store is up, however many instances receive copies.
- A crashed instance must not block a key forever.
- A slow instance whose claim has expired must not overwrite the result of the instance that took over.
- Redis is already the usual shared state in this kind of deployment, and the rate limiter needs it anyway.

## Considered options

1. Check-then-set: `HGETALL`, then `HSET` + `EXPIRE` if the key was absent. Two round trips, not atomic together.
2. `SET key value NX PX lease` for the claim, plus separate commands to read and store the result.
3. One Lua script per operation (claim, renew, complete, release) that reads and writes the hash atomically.
4. Redlock across several Redis primaries.
5. A relational database row with a unique constraint, inside the business transaction.

## Decision outcome

Chosen option: **3.**

Each script runs atomically on the primary, so the claim's read and write cannot interleave with another instance's.
The record is one hash: `state`, `fp`, `owner`, `fence`, and once completed `status`, `headers`, `body` and
`replayable`. The hash's TTL is the lease while the request runs and the retention once it is done, so Redis
garbage-collects both abandoned claims and expired results without a sweeper.

- **Owner token.** A random 128-bit value that only the claiming request knows. `complete`, `renew` and `release`
  succeed only with it. A request whose lease lapsed cannot complete a key that another instance now owns. The
  integration test `A_crashed_owner_blocks_the_key_only_until_its_lease_lapses` writes with a dead owner's token
  after takeover and gets `false`.
- **Lease and renewal.** A claim lasts 30 s and is renewed every 10 s while the handler runs, so only a dead or
  partitioned instance loses it. A renewal that comes back `false` cancels `IIdempotencyFeature.LeaseLost`, so
  long-running handlers can stop.
- **Fencing token.** `INCR` on a counter, returned with every granted claim. A lease cannot stop a paused process
  from waking up and writing after its lease has expired; only the downstream system can reject that write
  (Kleppmann, 2016). The middleware cannot fence your database for you. It hands the handler a token that strictly
  increases, so the database can (`UPDATE ... WHERE last_fence < @fence`).

Measured (two Kestrel instances, 30 rounds of 50 concurrent copies, `docs/benchmarks.md`):

| store | handler runs per round (median / min / max) | rounds with duplicates |
|---|---|---|
| no idempotency | 50 / 50 / 50 | 30 of 30 |
| check-then-set (option 1) | 9 / 4 / 15 | 30 of 30 |
| Lua scripts (option 3) | 1 / 1 / 1 | 0 of 30 |

Option 1 does not merely fail occasionally under load. It fails in every round. Option 2 makes the claim atomic, but
then the result has to be stored with a separate, unguarded write, which re-creates the stale-owner problem the owner
token solves. Option 4 (Redlock) multiplies round trips and still produces no fencing token. Option 5 is the right
answer when the operation's side effect lives entirely in one database: the claim and the side effect then commit
together. But it couples the middleware to the application's schema and transaction. It stays possible through
`IIdempotencyStore`.

### Consequences

- Good: single execution holds across instances. It is tested with 4 instances × 100 copies, 8 raw connections × 25
  claims, and the compose demo.
- Good: one round trip per decision: 0.96 ms p50 for a claim, against 0.85 ms for a bare `PING`, on this machine.
- Bad: Redis Cluster. The claim script touches two keys (the record and the fence counter), so both must hash to the
  same slot. `FenceKey` is configurable, and the limitation is in the README.
- Bad: a Redis failover with asynchronous replication can lose a recent claim. The new primary then lets the key run
  again. Lua cannot fix that; it is in the threat model and the limitations.
- Bad: if `complete` fails after the handler has run (Redis down at that exact moment), the lease lapses and a retry
  executes again. This is the one window where the guarantee degrades to at-least-once, and it is logged as an error.

## Links

- Kleppmann, *How to do distributed locking* (fencing tokens; why Redlock cannot provide them): https://martin.kleppmann.com/2016/02/08/how-to-do-distributed-locking.html
- Redis scripting and atomicity: https://redis.io/docs/latest/develop/interact/programmability/eval-intro/
- Measured comparison: `benchmark-results/duplicates.json`
