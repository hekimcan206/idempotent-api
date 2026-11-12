using IdempotentApi.RateLimiting;
using StackExchange.Redis;

namespace IdempotentApi.Benchmarks;

/// <summary>
/// The "obvious" Redis idempotency store: read the record, and if it is absent, write a claim. Each command is atomic
/// on its own, but the pair is not, so two requests can both read "absent" and both execute. It exists only to
/// measure that race next to the Lua store.
/// </summary>
internal sealed class CheckThenSetStore(IConnectionMultiplexer redis) : IIdempotencyStore
{
    public async ValueTask<BeginResult> TryBeginAsync(string storageKey, string fingerprint, TimeSpan lease, CancellationToken cancellationToken)
    {
        var db = redis.GetDatabase();
        var existing = await db.HashGetAllAsync(storageKey);
        if (existing.Length > 0)
        {
            var map = existing.ToDictionary(e => (string)e.Name!, e => e.Value);
            if ((string?)map["fp"] != fingerprint)
            {
                return BeginResult.Mismatch;
            }

            return (string?)map["state"] == "done"
                ? BeginResult.Completed(new StoredResponse((int)map["status"], [], (byte[])map["body"]!))
                : BeginResult.InFlight;
        }

        var owner = Guid.NewGuid().ToString("N");
        await db.HashSetAsync(storageKey, [new("state", "running"), new("fp", fingerprint), new("owner", owner)]);
        await db.KeyExpireAsync(storageKey, lease);
        return BeginResult.Acquired(new IdempotencyLease(owner, 0));
    }

    public async ValueTask<bool> RenewAsync(string storageKey, string ownerToken, TimeSpan lease, CancellationToken cancellationToken) =>
        await redis.GetDatabase().KeyExpireAsync(storageKey, lease);

    public async ValueTask<bool> CompleteAsync(string storageKey, string ownerToken, StoredResponse response, TimeSpan retention, CancellationToken cancellationToken)
    {
        var db = redis.GetDatabase();
        await db.HashSetAsync(storageKey, [new("state", "done"), new("status", response.StatusCode), new("body", response.Body.ToArray())]);
        await db.KeyExpireAsync(storageKey, retention);
        return true;
    }

    public async ValueTask<bool> ReleaseAsync(string storageKey, string ownerToken, CancellationToken cancellationToken) =>
        await redis.GetDatabase().KeyDeleteAsync(storageKey);
}

/// <summary>Token bucket computed in the application: GET state, decide in C#, SET state. Same math, no atomicity.</summary>
internal sealed class GetComputeSetBuckets(IConnectionMultiplexer redis) : ITokenBucketStore
{
    public async ValueTask<TokenBucketDecision> TakeAsync(string bucketKey, TokenBucketPolicy policy, int cost, CancellationToken cancellationToken)
    {
        var db = redis.GetDatabase();
        var fields = await db.HashGetAsync(bucketKey, ["tokens", "ts"]);
        TokenBucketState? state = fields[0].IsNull ? null : new TokenBucketState((double)fields[0], (long)fields[1]);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
        var (next, decision) = TokenBucketMath.Take(state, now, policy, cost);
        await db.HashSetAsync(bucketKey, [new("tokens", next.Tokens), new("ts", next.UpdatedAtMicros)]);
        return decision;
    }
}
