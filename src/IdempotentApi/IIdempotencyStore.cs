namespace IdempotentApi;

/// <summary>
/// Durable state behind the middleware. Every method must be atomic with respect to every other call on the same key,
/// across all application instances that share the store; that atomicity is what turns N concurrent requests with one
/// key into exactly one execution.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Atomically claims <paramref name="storageKey"/> for execution, or reports why it cannot be claimed.
    /// A claim is a lease: it disappears after <paramref name="lease"/> unless renewed or completed, so a crashed
    /// instance cannot block a key forever.
    /// </summary>
    ValueTask<BeginResult> TryBeginAsync(string storageKey, string fingerprint, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>Extends a held lease. Returns false when the lease was lost (expired, released or taken over).</summary>
    ValueTask<bool> RenewAsync(string storageKey, string ownerToken, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the final response and keeps it for <paramref name="retention"/>. Returns false, and stores nothing,
    /// when the caller no longer owns the lease: a late writer must never overwrite a newer owner's outcome.
    /// </summary>
    ValueTask<bool> CompleteAsync(string storageKey, string ownerToken, StoredResponse response, TimeSpan retention, CancellationToken cancellationToken);

    /// <summary>Drops a held lease without a result so a retry can execute again. No-op when not the owner.</summary>
    ValueTask<bool> ReleaseAsync(string storageKey, string ownerToken, CancellationToken cancellationToken);
}

/// <summary>What <see cref="IIdempotencyStore.TryBeginAsync"/> found.</summary>
public enum BeginOutcome
{
    /// <summary>The caller now owns the key and must execute the request.</summary>
    Acquired,

    /// <summary>Another request with the same key and fingerprint is executing right now.</summary>
    InFlight,

    /// <summary>A request with the same key and fingerprint already finished; its response is attached.</summary>
    Completed,

    /// <summary>The key is (or was) used with a different request payload.</summary>
    FingerprintMismatch,
}

/// <summary>Result of a claim attempt.</summary>
public sealed record BeginResult(BeginOutcome Outcome, IdempotencyLease? Lease = null, StoredResponse? Response = null)
{
    public static BeginResult InFlight { get; } = new(BeginOutcome.InFlight);

    public static BeginResult Mismatch { get; } = new(BeginOutcome.FingerprintMismatch);

    public static BeginResult Acquired(IdempotencyLease lease) => new(BeginOutcome.Acquired, lease);

    public static BeginResult Completed(StoredResponse response) => new(BeginOutcome.Completed, Response: response);
}

/// <summary>
/// A held claim. <see cref="OwnerToken"/> is a random value only the holder knows (so nobody else can complete or
/// release the key); <see cref="FencingToken"/> increases with every claim the store grants, so downstream systems
/// can reject writes from a holder whose lease has silently expired.
/// </summary>
public sealed record IdempotencyLease(string OwnerToken, long FencingToken);

/// <summary>A response as it is replayed to retries.</summary>
/// <param name="StatusCode">HTTP status of the original response.</param>
/// <param name="Headers">Headers worth replaying (hop-by-hop, cookies and per-response headers are excluded).</param>
/// <param name="Body">The response body bytes.</param>
/// <param name="Replayable">
/// False when the original response was larger than the configured limit: the request did execute, so it must not
/// execute again, but there is nothing to replay.
/// </param>
public sealed record StoredResponse(int StatusCode, IReadOnlyList<KeyValuePair<string, string>> Headers, ReadOnlyMemory<byte> Body, bool Replayable = true);
