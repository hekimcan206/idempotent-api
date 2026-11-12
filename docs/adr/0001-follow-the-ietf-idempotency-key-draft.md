# 0001. Follow the IETF Idempotency-Key draft, strictly by default

- Status: accepted
- Date: 2026-11-12

## Context and problem statement

Clients retry `POST` and `PATCH` after timeouts and dropped connections. Without server-side deduplication a retry can
charge a card twice. Many APIs solve this with an `Idempotency-Key` header, but each one has slightly different rules:
the key format, what counts as "the same request", which status codes are returned, and how long keys live. The IETF
HTTPAPI working group is standardising the header in draft-ietf-httpapi-idempotency-key-header (revision 07,
October 2025, at the time of writing). The middleware has to pick a set of rules and say which ones it chose.

## Decision drivers

- Interoperability: clients written against the draft should work without reading our code.
- Safety: one key from one client must never return another client's response, and must never be confused with
  another request.
- A clear, documented contract, because the draft requires resources to publish their idempotency specification.

## Considered options

1. Implement the draft's semantics, with a strict key syntax.
2. Use a provider-style convention: bare keys, and a single "conflict" status for every error.
3. Implement the draft's semantics, but accept any header value.

## Decision outcome

Chosen option: **1, with an opt-in leniency switch.**

- **Syntax.** The key is an sf-string (RFC 9651, Structured Field Item of type String), as the draft says it MUST be.
  The parser rejects parameters, list syntax and non-ASCII characters instead of normalising them. Bare (unquoted)
  keys are refused by default, because accepting them silently would hide non-compliant clients. `AllowBareKeys`
  exists because many deployed clients send bare UUIDs.
- **Status codes, as in the draft.** 400 for a missing or invalid key. 422 when a key is reused with a different
  fingerprint. 409 while the original request is still in flight. Error bodies are RFC 9457 problem documents, and
  their `type` URIs link to `docs/idempotency-specification.md`, which is the published specification the draft asks for.
- **Fingerprint.** SHA-256 over method, path, query, normalised media type and the exact body bytes. The fields are
  length-prefixed so that no two different requests produce the same input. The draft leaves the fingerprint method
  to the resource. Byte-for-byte comparison is predictable, and it costs 1.1 µs for a 1 KiB body and 32 µs for 64 KiB (see
  `docs/benchmarks.md`). Semantic JSON comparison would need a parser and an equality definition for every media
  type, so it is left to a custom `IRequestFingerprinter`.
- **Scoping.** The storage key is SHA-256(length-prefixed partition, key), and the partition is the caller's
  identity. This is the draft's mitigation against cross-client data leaks. Hashing also keeps client-chosen bytes out
  of the Redis key space: no injection, and bounded key length.
- **The storage key does not include method or path.** Reusing one key on another endpoint is therefore answered
  with 422, the draft's reuse error, instead of starting a second, independent operation.
- **Minimum key length 16.** The draft recommends UUIDs. Refusing short keys catches clients that use counters.

### Consequences

- Good: a client written against the draft gets the documented status codes, and every error body names the rule it broke.
- Good: golden files pin all seven error responses, so a wording change is a reviewed diff.
- Bad: strict syntax rejects some clients that work with other APIs. The switch exists, and the 400 detail shows a
  correct example value.
- Bad: the draft can still change before it becomes an RFC. The status codes have been stable across revisions; the
  exact problem types are ours, because the draft only gives examples.

## Links

- draft-ietf-httpapi-idempotency-key-header-07: https://datatracker.ietf.org/doc/html/draft-ietf-httpapi-idempotency-key-header-07
- Draft revision history: https://datatracker.ietf.org/doc/draft-ietf-httpapi-idempotency-key-header/
- RFC 9651 Structured Field Values for HTTP: https://www.rfc-editor.org/rfc/rfc9651
- RFC 9457 Problem Details for HTTP APIs: https://www.rfc-editor.org/rfc/rfc9457
- Stripe's idempotent requests (24-hour retention, result stored even on failure): https://docs.stripe.com/api/idempotent_requests
