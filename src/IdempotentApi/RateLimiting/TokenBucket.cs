namespace IdempotentApi.RateLimiting;

/// <summary>
/// A token bucket: up to <see cref="Capacity"/> requests in a burst, refilled continuously at
/// <see cref="RefillPerSecond"/>. Over any interval of length T a bucket admits at most Capacity + RefillPerSecond * T.
/// </summary>
public sealed record TokenBucketPolicy
{
    public TokenBucketPolicy(string name, int capacity, double refillPerSecond)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        if (!(refillPerSecond > 0) || double.IsInfinity(refillPerSecond))
        {
            throw new ArgumentOutOfRangeException(nameof(refillPerSecond), "The refill rate must be a positive, finite number.");
        }

        Name = name;
        Capacity = capacity;
        RefillPerSecond = refillPerSecond;
    }

    /// <summary>Policy name, published in the <c>RateLimit-Policy</c> header.</summary>
    public string Name { get; }

    public int Capacity { get; }

    public double RefillPerSecond { get; }

    /// <summary>Seconds an empty bucket needs to fill up; the <c>w</c> of <c>RateLimit-Policy</c>.</summary>
    public int WindowSeconds => (int)Math.Ceiling(Capacity / RefillPerSecond);
}

/// <summary>Outcome of one take.</summary>
/// <param name="Allowed">Whether the request may proceed.</param>
/// <param name="Remaining">Whole tokens left after this take.</param>
/// <param name="RetryAfter">When denied, how long until enough tokens exist.</param>
/// <param name="ResetAfter">How long until the bucket is full again.</param>
public readonly record struct TokenBucketDecision(bool Allowed, long Remaining, TimeSpan RetryAfter, TimeSpan ResetAfter);

/// <summary>Bucket state: fractional tokens and the time (microseconds) they were last computed.</summary>
public readonly record struct TokenBucketState(double Tokens, long UpdatedAtMicros);

/// <summary>
/// The pure algorithm. The Redis Lua script implements the same arithmetic; this copy backs the in-memory store and
/// the property tests that state the bucket's guarantees.
/// </summary>
public static class TokenBucketMath
{
    internal const double Tolerance = 1e-9;

    public static (TokenBucketState State, TokenBucketDecision Decision) Take(TokenBucketState? current, long nowMicros, TokenBucketPolicy policy, int cost)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var tokens = current?.Tokens ?? policy.Capacity;
        var updatedAt = current?.UpdatedAtMicros ?? nowMicros;

        // Never refill backwards: if the clock steps back (failover to a replica with a slower clock) the bucket
        // simply waits for time to catch up instead of minting tokens.
        if (nowMicros > updatedAt)
        {
            tokens = Math.Min(policy.Capacity, tokens + ((nowMicros - updatedAt) * policy.RefillPerSecond / 1_000_000d));
            updatedAt = nowMicros;
        }

        // A request retried exactly at RetryAfter must succeed; without the tolerance, floating-point refill can land
        // a hair below the cost (0.99999999 tokens) and deny it.
        var allowed = tokens >= cost - Tolerance;
        var retryMicros = 0L;
        if (allowed)
        {
            tokens = Math.Max(0, tokens - cost);
        }
        else
        {
            retryMicros = (long)Math.Ceiling((cost - tokens) * 1_000_000d / policy.RefillPerSecond);
        }

        var resetMicros = (long)Math.Ceiling((policy.Capacity - tokens) * 1_000_000d / policy.RefillPerSecond);
        var decision = new TokenBucketDecision(
            allowed,
            (long)Math.Floor(tokens),
            TimeSpan.FromMicroseconds(retryMicros),
            TimeSpan.FromMicroseconds(resetMicros));
        return (new TokenBucketState(tokens, updatedAt), decision);
    }
}
