# IdempotentApi

ASP.NET Core middleware for the IETF `Idempotency-Key` header (draft-ietf-httpapi-idempotency-key-header):
request fingerprinting, exactly one execution for concurrent requests with the same key, response replay, and
token-bucket rate limiting with the IETF `RateLimit` / `RateLimit-Policy` headers.

- `IdempotentApi.AspNetCore`: middleware, in-memory stores.
- `IdempotentApi.Redis`: Redis stores (atomic Lua scripts) for multi-instance deployments.

```csharp
builder.Services.AddIdempotency();
builder.Services.AddRedisIdempotencyStore("localhost:6379");

app.UseIdempotency();
app.MapPost("/payments", CreatePayment).RequireIdempotency();
```

Documentation, design decisions and measured results: https://github.com/hekimcan206/idempotent-api
