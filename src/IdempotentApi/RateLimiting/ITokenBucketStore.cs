namespace IdempotentApi.RateLimiting;

/// <summary>Where bucket state lives. A take must be atomic: read, refill, decide and write as one step.</summary>
public interface ITokenBucketStore
{
    ValueTask<TokenBucketDecision> TakeAsync(string bucketKey, TokenBucketPolicy policy, int cost, CancellationToken cancellationToken);
}

/// <summary>Per-process buckets. Each instance enforces its own limit, so N instances admit up to N times the policy.</summary>
public sealed class InMemoryTokenBucketStore(TimeProvider timeProvider) : ITokenBucketStore
{
    private readonly Dictionary<string, TokenBucketState> _buckets = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public InMemoryTokenBucketStore()
        : this(TimeProvider.System)
    {
    }

    public ValueTask<TokenBucketDecision> TakeAsync(string bucketKey, TokenBucketPolicy policy, int cost, CancellationToken cancellationToken)
    {
        var nowMicros = timeProvider.GetUtcNow().ToUnixTimeMilliseconds() * 1000;
        lock (_gate)
        {
            TokenBucketState? current = _buckets.TryGetValue(bucketKey, out var state) ? state : null;
            var (next, decision) = TokenBucketMath.Take(current, nowMicros, policy, cost);

            // A full bucket carries no information, so it is dropped; this bounds memory to recently active clients.
            if (next.Tokens >= policy.Capacity)
            {
                _buckets.Remove(bucketKey);
            }
            else
            {
                _buckets[bucketKey] = next;
            }

            return ValueTask.FromResult(decision);
        }
    }
}
