using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace IdempotentApi.Redis;

/// <summary>Settings for <see cref="RedisIdempotencyStore"/>.</summary>
public sealed class RedisIdempotencyStoreOptions
{
    /// <summary>Logical database number; -1 uses the connection's default.</summary>
    public int Database { get; set; } = -1;

    /// <summary>
    /// The counter that hands out fencing tokens. Every claim script touches this key and the record key, so on Redis
    /// Cluster both must hash to one slot (give them a common <c>{hash tag}</c>) or the script is rejected.
    /// </summary>
    public string FenceKey { get; set; } = "idem:fence";
}

/// <summary>
/// <see cref="IIdempotencyStore"/> on Redis. Each operation is one Lua script, so the check and the write happen in a
/// single atomic step on the primary: two instances can never both see "absent" and both execute.
/// </summary>
/// <remarks>
/// Record layout: one hash per key with <c>state</c> (running|done), <c>fp</c>, <c>owner</c>, <c>fence</c>, and after
/// completion <c>status</c>, <c>headers</c>, <c>body</c>, <c>replayable</c>. The hash's TTL is the lease while running
/// and the retention once done, so Redis itself garbage-collects abandoned and expired keys.
/// </remarks>
public sealed class RedisIdempotencyStore(IConnectionMultiplexer redis, RedisIdempotencyStoreOptions? options = null) : IIdempotencyStore
{
    private readonly RedisIdempotencyStoreOptions _options = options ?? new RedisIdempotencyStoreOptions();

    private IDatabase Db => redis.GetDatabase(_options.Database);

    public async ValueTask<BeginResult> TryBeginAsync(string storageKey, string fingerprint, TimeSpan lease, CancellationToken cancellationToken)
    {
        var owner = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var result = (RedisResult[])(await Db.ScriptEvaluateAsync(
            LuaScripts.Begin,
            [storageKey, _options.FenceKey],
            [fingerprint, owner, Milliseconds(lease)]))!;

        return (int)result[0] switch
        {
            1 => BeginResult.Acquired(new IdempotencyLease(owner, (long)result[1])),
            2 => BeginResult.InFlight,
            3 => BeginResult.Completed(new StoredResponse(
                StatusCode: (int)result[1],
                Headers: DecodeHeaders((string?)result[2]),
                Body: (byte[]?)result[3] ?? [],
                Replayable: (string?)result[4] == "1")),
            4 => BeginResult.Mismatch,
            var other => throw new InvalidOperationException($"Unexpected begin.lua result {other}."),
        };
    }

    public async ValueTask<bool> RenewAsync(string storageKey, string ownerToken, TimeSpan lease, CancellationToken cancellationToken) =>
        (int)await Db.ScriptEvaluateAsync(LuaScripts.Renew, [storageKey], [ownerToken, Milliseconds(lease)]) == 1;

    public async ValueTask<bool> CompleteAsync(string storageKey, string ownerToken, StoredResponse response, TimeSpan retention, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        var result = await Db.ScriptEvaluateAsync(
            LuaScripts.Complete,
            [storageKey],
            [
                ownerToken,
                response.StatusCode,
                EncodeHeaders(response.Headers),
                response.Body.ToArray(),
                response.Replayable ? "1" : "0",
                Milliseconds(retention),
            ]);
        return (int)result == 1;
    }

    public async ValueTask<bool> ReleaseAsync(string storageKey, string ownerToken, CancellationToken cancellationToken) =>
        (int)await Db.ScriptEvaluateAsync(LuaScripts.Release, [storageKey], [ownerToken]) == 1;

    private static long Milliseconds(TimeSpan span) => Math.Max(1, (long)Math.Ceiling(span.TotalMilliseconds));

    // HTTP field names and values cannot contain CR or LF, so a newline-separated list is unambiguous and cheaper
    // than JSON on every replay.
    internal static string EncodeHeaders(IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        var sb = new StringBuilder();
        foreach (var (name, value) in headers)
        {
            if (name.AsSpan().ContainsAny('\r', '\n') || value.AsSpan().ContainsAny('\r', '\n'))
            {
                continue;
            }

            sb.Append(name).Append('\n').Append(value).Append('\n');
        }

        return sb.ToString();
    }

    internal static List<KeyValuePair<string, string>> DecodeHeaders(string? encoded)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrEmpty(encoded))
        {
            return list;
        }

        var parts = encoded.Split('\n');
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            list.Add(new(parts[i], parts[i + 1]));
        }

        return list;
    }
}
