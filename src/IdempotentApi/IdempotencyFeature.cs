namespace IdempotentApi;

/// <summary>
/// Exposed on <c>HttpContext.Features</c> while an idempotent request executes, so a handler can pass the fencing token
/// to a database (for example a <c>WHERE last_fence &lt; @fence</c> guard) and reject writes from an owner whose lease
/// has expired.
/// </summary>
public interface IIdempotencyFeature
{
    /// <summary>The key as sent by the client.</summary>
    string Key { get; }

    /// <summary>The partition-scoped, hashed key used in the store.</summary>
    string StorageKey { get; }

    /// <summary>Monotonically increasing per granted claim. Larger means newer.</summary>
    long FencingToken { get; }

    /// <summary>
    /// Becomes cancelled when lease renewal fails, which means another instance may take over the key. Long handlers
    /// should observe it and stop before side effects they cannot fence.
    /// </summary>
    CancellationToken LeaseLost { get; }
}

internal sealed class IdempotencyFeature(string key, string storageKey, long fencingToken, CancellationToken leaseLost) : IIdempotencyFeature
{
    public string Key { get; } = key;

    public string StorageKey { get; } = storageKey;

    public long FencingToken { get; } = fencingToken;

    public CancellationToken LeaseLost { get; } = leaseLost;
}
