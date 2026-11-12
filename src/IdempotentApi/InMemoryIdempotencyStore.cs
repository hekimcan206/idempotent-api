using System.Security.Cryptography;

namespace IdempotentApi;

/// <summary>
/// A single-process store. Correct for one instance (and for tests); it gives no guarantee across instances, which is
/// what <c>IdempotentApi.Redis</c> is for.
/// </summary>
public sealed class InMemoryIdempotencyStore(TimeProvider timeProvider) : IIdempotencyStore
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private long _fence;
    private long _operations;

    public InMemoryIdempotencyStore()
        : this(TimeProvider.System)
    {
    }

    /// <summary>Number of live entries (expired ones are removed lazily).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public ValueTask<BeginResult> TryBeginAsync(string storageKey, string fingerprint, TimeSpan lease, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            SweepOccasionally(now);
            if (_entries.TryGetValue(storageKey, out var entry) && entry.ExpiresAt > now)
            {
                if (!string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return ValueTask.FromResult(BeginResult.Mismatch);
                }

                return ValueTask.FromResult(entry.Response is { } response ? BeginResult.Completed(response) : BeginResult.InFlight);
            }

            var granted = new IdempotencyLease(NewOwnerToken(), ++_fence);
            _entries[storageKey] = new Entry(fingerprint, granted.OwnerToken, now + lease, null);
            return ValueTask.FromResult(BeginResult.Acquired(granted));
        }
    }

    public ValueTask<bool> RenewAsync(string storageKey, string ownerToken, TimeSpan lease, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (!IsLiveOwner(storageKey, ownerToken, now, out var entry))
            {
                return ValueTask.FromResult(false);
            }

            _entries[storageKey] = entry with { ExpiresAt = now + lease };
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<bool> CompleteAsync(string storageKey, string ownerToken, StoredResponse response, TimeSpan retention, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (!IsLiveOwner(storageKey, ownerToken, now, out var entry))
            {
                return ValueTask.FromResult(false);
            }

            _entries[storageKey] = entry with { Owner = null, Response = response, ExpiresAt = now + retention };
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<bool> ReleaseAsync(string storageKey, string ownerToken, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (!IsLiveOwner(storageKey, ownerToken, now, out _))
            {
                return ValueTask.FromResult(false);
            }

            _entries.Remove(storageKey);
            return ValueTask.FromResult(true);
        }
    }

    private bool IsLiveOwner(string storageKey, string ownerToken, DateTimeOffset now, out Entry entry)
    {
        return _entries.TryGetValue(storageKey, out entry!)
            && entry.ExpiresAt > now
            && entry.Response is null
            && string.Equals(entry.Owner, ownerToken, StringComparison.Ordinal);
    }

    private void SweepOccasionally(DateTimeOffset now)
    {
        // Amortised cleanup: a full scan every 1,024 claims keeps memory bounded without a background timer.
        if (++_operations % 1024 != 0)
        {
            return;
        }

        foreach (var key in _entries.Where(e => e.Value.ExpiresAt <= now).Select(e => e.Key).ToList())
        {
            _entries.Remove(key);
        }
    }

    private static string NewOwnerToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private sealed record Entry(string Fingerprint, string? Owner, DateTimeOffset ExpiresAt, StoredResponse? Response);
}
