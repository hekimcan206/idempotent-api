# 0004. What is stored and replayed, and failing closed when the store is down

- Status: accepted
- Date: 2026-11-12

## Context and problem statement

After the handler runs, the middleware decides whether to keep the outcome. A kept outcome is replayed to every retry
for 24 hours. A dropped outcome lets the next retry execute again. A wrong decision in either direction is a bug:
replaying a transient 503 for a day breaks the client, and re-executing after a successful charge charges twice. We
also have to decide what happens when Redis cannot be reached at all.

## Decisions

### Statuses

- **Stored:** every status below 500, except 408, 409, 425 and 429. This includes client errors such as 400, 402 and
  422: the draft says the result of the original operation is returned "whether success or error". A request that
  failed validation will fail the same way on retry.
- **Released (the key is freed, the next retry executes):** 5xx, exceptions, 408, 409, 425 and 429. These say
  "this attempt did not complete", not "this is the outcome". Caching them would turn a transient fault into a
  permanent one. `IsCacheableStatus` can be replaced.
- This differs from APIs that store 5xx responses too. Those APIs instead accept that an internal error stays
  "final" for the key's lifetime. We judged that a client which cannot get past a stored 500 is worse than a handler
  that must be safe to re-run after a crash, and handlers should be safe to re-run in any case: a crash at the wrong
  moment has the same effect.

### Headers

- **Replayed:** status, body and every header except a denylist: `Date`, `Server`, `Content-Length`,
  `Transfer-Encoding`, `Connection`, `Keep-Alive`, `Set-Cookie`, `Retry-After`, `RateLimit*` and trace headers.
  These describe one transmission. A replayed `Set-Cookie` would hand a session to whoever holds the key.
- Every replay carries `Idempotent-Replayed: true`. The draft does not define a replay marker. This one lets clients,
  logs and the demo tell a replay from a fresh execution.

### Size limits

- **Request body: 1 MiB.** The whole body must be buffered to fingerprint it. Larger bodies get 413 before
  anything runs.
- **Response body: 1 MiB stored.** The response streams to the client as the handler writes it, and a bounded copy
  is kept. If the copy overflows, the outcome is recorded as "executed, not replayable": retries get 409
  `not-replayable` with the original status, never a second execution. The alternative, releasing the key, would make
  large responses silently non-idempotent.

### Store outage

- **Idempotency fails closed: 503 with `Retry-After: 1`, and the handler does not run.** Without the store there is
  no way to know whether the key already executed. Running anyway would make idempotency a best-effort feature
  exactly when retries are most likely, during an incident.
- **Rate limiting fails open by default** (ADR 0005). The asymmetry is deliberate: a limiter protects capacity,
  while idempotency protects correctness.
- If the store fails *after* the handler ran, so that `complete` throws, the client still gets its response. The
  lease lapses, and a retry would execute again. This is logged as an error, and it is the one window where the
  guarantee degrades to at-least-once.

### Client disconnects

- The outcome is stored even if the client went away mid-response, using `CancellationToken.None` for `complete`.
  The retry that follows a dropped connection is exactly the request that must get the stored answer.

## Consequences

- Good: every row of the table above has a test (`MiddlewareTests`, `GoldenProblemTests`, `MultiInstanceTests`).
- Bad: handlers must be safe to re-run after a 5xx or an exception. This is documented, and it is true of any
  crash-prone system.
- Bad: the 1 MiB limits suit JSON APIs, not file uploads. Both limits are options.

## Links

- draft-ietf-httpapi-idempotency-key-header-07 (retry returns the result "whether success or error"): https://datatracker.ietf.org/doc/html/draft-ietf-httpapi-idempotency-key-header-07
- Stripe's choice to store failed results as well, for contrast: https://docs.stripe.com/api/idempotent_requests
