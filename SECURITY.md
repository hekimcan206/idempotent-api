# Security policy

## Supported versions

Only the latest release receives fixes.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private vulnerability reporting instead:
**Security → Report a vulnerability** on this repository. You can expect an acknowledgement within 5 working days. If
the report is confirmed, a fix or mitigation plan follows within 30 days.

Please include the affected version or commit, steps to reproduce and the impact you observed. A broken
single-execution guarantee (a handler running twice for one key while Redis is healthy) is treated as a security issue.

## Security-relevant design

See [docs/threat-model.md](docs/threat-model.md) for assets, threats and mitigations. In short:

| Area | Default |
|---|---|
| Concurrent duplicates | Atomic Lua claim; owner token on complete, renew and release; leases with renewal; fencing token for downstream writes |
| Cross-client replay | Keys scoped to the caller's partition and hashed with it; minimum key length 16 |
| Key injection | Strict sf-string parsing (at most 255 characters); Redis sees only `prefix:<sha256>` |
| Key reuse | SHA-256 fingerprint over method, path, query, media type and body; mismatch gives 422 |
| Replayed headers | `Set-Cookie`, `Date`, rate-limit and trace headers are never stored |
| Store outage | Idempotency fails closed (503, nothing executed); rate limiting fails open by default (configurable) |
| Input limits | 1 MiB request body (413), 1 MiB stored response |
| Data at rest | Stored responses expire after 24 h; not encrypted by the library (use Redis ACLs and TLS) |
| Secrets | None in the repository. The sample uses an unauthenticated Redis on a private compose network for development only |
