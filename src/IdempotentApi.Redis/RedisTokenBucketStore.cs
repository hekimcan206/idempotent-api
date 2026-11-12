using System.Globalization;
using IdempotentApi.RateLimiting;
using StackExchange.Redis;

namespace IdempotentApi.Redis;

/// <summary>
/// One token bucket per key, shared by every application instance. A take is a single Lua script (read, refill,
/// decide, write), so concurrent instances cannot each read "1 token left" and both spend it.
/// </summary>
public sealed class RedisTokenBucketStore(IConnectionMultiplexer redis, int database = -1) : ITokenBucketStore
{
    public async ValueTask<TokenBucketDecision> TakeAsync(string bucketKey, TokenBucketPolicy policy, int cost, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var result = (RedisResult[])(await redis.GetDatabase(database).ScriptEvaluateAsync(
            LuaScripts.TokenBucket,
            [bucketKey],
            [policy.Capacity, policy.RefillPerSecond.ToString("R", CultureInfo.InvariantCulture), cost]))!;

        var tokens = double.Parse((string)result[1]!, NumberStyles.Float, CultureInfo.InvariantCulture);
        return new TokenBucketDecision(
            Allowed: (int)result[0] == 1,
            Remaining: (long)Math.Floor(tokens),
            RetryAfter: TimeSpan.FromMicroseconds((long)result[2]),
            ResetAfter: TimeSpan.FromMicroseconds((long)result[3]));
    }
}
