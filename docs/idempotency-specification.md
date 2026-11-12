# Idempotency specification

draft-ietf-httpapi-idempotency-key-header-07 says a resource MUST publish its idempotency specification, including
expiry and error behaviour. This page is that specification for APIs built with `IdempotentApi.AspNetCore` on default
settings. The problem `type` URIs the middleware emits point to the anchors below. An API that changes the defaults
should publish its own copy with its own values.

## Which requests are covered

- Endpoints marked with `RequireIdempotency()`. Other endpoints are not touched.
- Unsafe methods only. `GET`, `HEAD`, `OPTIONS` and `TRACE` pass through even on a marked endpoint.

## The key

```http
Idempotency-Key: "8e03978e-40d5-43e8-bc93-6894a57f9324"
```

- The key is a Structured Field String (RFC 9651 / RFC 8941): double-quoted printable ASCII, where only `\"` and `\\` are escaped.
- It is 16 to 255 characters long. Use a random UUID (v4 or v7). Predictable keys such as `retry-1` are refused.
- Send exactly one field, with no parameters.
- Keys are scoped to the caller: the authenticated user, or whatever the API's partition resolver returns (an API key id, say). Two callers that pick the same key get two independent operations.
- Unquoted keys are refused unless the API turns on `AllowBareKeys`.

## The fingerprint

A retry counts as the same request only if all of these match the original:

- the method
- the path
- the query string
- the media type (parameters such as `charset` and letter case are ignored)
- the body, byte for byte

The body is not parsed: re-serialising JSON with different whitespace or key order makes it a different request.

## Responses

| Situation | Status | Problem type anchor |
|---|---|---|
| First request with this key | Whatever the handler returns | none |
| Retry after the original completed (2xx, or 4xx other than 408/409/425/429) | The original status, headers and body, plus `Idempotent-Replayed: true` | none |
| Retry after the original failed with 5xx, threw, or returned 408/409/425/429 | The request executes again | none |
| Key missing on an endpoint that requires one | 400 | [`#missing-key`](#missing-key) |
| Key malformed, too short or too long, or sent twice | 400 | [`#invalid-key`](#invalid-key) |
| Same key, different fingerprint | 422 | [`#key-reused`](#key-reused) |
| Same key while the original is still running | 409 with `Retry-After: 1` (reject mode) | [`#request-in-flight`](#request-in-flight) |
| The original ran, but its response was larger than 1 MiB | 409 with `original-status` | [`#not-replayable`](#not-replayable) |
| Request body larger than 1 MiB | 413 | [`#body-too-large`](#body-too-large) |
| Idempotency store unreachable | 503 with `Retry-After: 1`; nothing executed | [`#store-unavailable`](#store-unavailable) |

Endpoints in wait mode hold a concurrent duplicate until the original finishes (at most 10 s), then replay its
response. If the original fails, the waiting request executes instead. If the wait times out, it gets the same 409
as in reject mode.

### missing-key

The endpoint is idempotent and requires the header. Generate a new UUID per logical operation and send it on every
retry of that operation.

### invalid-key

The value is not a single Structured Field String of 16 to 255 printable ASCII characters. The `detail` member says
which rule failed.

### key-reused

The key was already used for a different request: a different method, path, query, media type or body. Keys
identify one operation. Use a new key for a new operation. Do not change the body when you retry.

### request-in-flight

Another request with this key is still being processed. Retry after `Retry-After` seconds to receive its response.

### not-replayable

The original request was executed, but its response was too large to store. The operation is not executed a second
time. Look up its result through the resource itself (for example a `GET` on the created resource).

### body-too-large

The middleware fingerprints the whole body, so it caps the body size it will buffer.

### store-unavailable

The server could not check whether this key was already processed, so it did not execute the request. Retrying
with the same key is safe.

## Retention

- A completed response is kept for 24 hours after completion. After that, the same key executes as a new request.
- A claim (a request that is running) lasts 30 seconds and is renewed every 10 seconds while the handler runs. If the
  instance that owns a claim dies, the key becomes usable again within 30 seconds.

## What is never replayed

`Set-Cookie`, `Date`, `Server`, `Content-Length`, `Transfer-Encoding`, `Connection`, `Keep-Alive`, `Retry-After`,
`RateLimit`, `RateLimit-Policy` and trace headers. These describe a single transmission, not the outcome of the
operation.
