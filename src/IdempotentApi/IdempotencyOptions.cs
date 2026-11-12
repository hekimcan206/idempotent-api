using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace IdempotentApi;

/// <summary>What to do with a request whose key is already executing elsewhere.</summary>
public enum ConcurrencyMode
{
    /// <summary>Answer 409 Conflict immediately, as the IETF draft specifies. The client retries later.</summary>
    Reject,

    /// <summary>
    /// Hold the duplicate until the first execution finishes (up to <see cref="IdempotencyPolicy.WaitTimeout"/>) and
    /// replay its response. Still a single execution, but clients that fire duplicates never see a 409.
    /// </summary>
    WaitForResult,
}

/// <summary>Per-endpoint behaviour. Attach with <c>RequireIdempotency()</c>.</summary>
public sealed class IdempotencyPolicy
{
    /// <summary>Reject requests without an <c>Idempotency-Key</c> with 400. When false they pass through unprotected.</summary>
    public bool KeyRequired { get; set; } = true;

    /// <summary>How long a completed response is kept and replayed. Publish this value to API consumers.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How long a claim survives without renewal. The middleware renews it every third of this period while the
    /// handler runs, so it only lapses when the owning instance stops (crash, pause, partition).
    /// </summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromSeconds(30);

    public ConcurrencyMode ConcurrencyMode { get; set; } = ConcurrencyMode.Reject;

    /// <summary>Upper bound on how long <see cref="ConcurrencyMode.WaitForResult"/> holds a duplicate before answering 409.</summary>
    public TimeSpan WaitTimeout { get; set; } = TimeSpan.FromSeconds(10);

    internal void Validate()
    {
        if (Retention <= TimeSpan.Zero || Lease <= TimeSpan.Zero || WaitTimeout < TimeSpan.Zero)
        {
            throw new InvalidOperationException("Idempotency retention and lease must be positive and the wait timeout non-negative.");
        }

        if (Lease < TimeSpan.FromMilliseconds(300))
        {
            throw new InvalidOperationException("An idempotency lease shorter than 300 ms would be renewed more often than a store round trip allows.");
        }
    }
}

/// <summary>Middleware-wide settings.</summary>
public sealed class IdempotencyOptions
{
    public const string DefaultHeaderName = "Idempotency-Key";

    public string HeaderName { get; set; } = DefaultHeaderName;

    /// <summary>Accept unquoted keys. The draft requires an sf-string; many existing clients send bare UUIDs.</summary>
    public bool AllowBareKeys { get; set; }

    /// <summary>Minimum decoded key length. 16 rejects obviously guessable keys such as "1" or "retry".</summary>
    public int MinKeyLength { get; set; } = 16;

    public int MaxKeyLength { get; set; } = 255;

    /// <summary>Requests with larger bodies are answered 413: the whole body must be buffered to fingerprint it.</summary>
    public long MaxRequestBodyBytes { get; set; } = 1024 * 1024;

    /// <summary>Responses larger than this are not stored; retries then get 409 instead of a second execution.</summary>
    public int MaxResponseBodyBytes { get; set; } = 1024 * 1024;

    /// <summary>Prefix of every storage key, so several APIs can share one Redis without colliding.</summary>
    public string KeyPrefix { get; set; } = "idem";

    /// <summary>
    /// Scopes keys to a caller. Two clients that happen to pick the same key must never see each other's responses
    /// (the draft's data-leak consideration). Defaults to the authenticated user's name identifier, else "anonymous";
    /// APIs with API keys should return the key's id here.
    /// </summary>
    public Func<HttpContext, string> PartitionResolver { get; set; } = DefaultPartition;

    /// <summary>Statuses that are stored and replayed. Default: everything below 500 except 408, 409, 425 and 429.</summary>
    public Func<int, bool> IsCacheableStatus { get; set; } = DefaultIsCacheable;

    /// <summary>Base URI of the problem types this middleware emits. Each type is this value plus a fragment.</summary>
    public Uri ProblemTypeBase { get; set; } = new("https://github.com/hekimcan206/idempotent-api/blob/main/docs/idempotency-specification.md");

    /// <summary>Applied to endpoints that call <c>RequireIdempotency()</c> without their own configuration.</summary>
    public IdempotencyPolicy DefaultPolicy { get; set; } = new();

    internal static string DefaultPartition(HttpContext context)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            return user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.Identity.Name ?? "authenticated";
        }

        return "anonymous";
    }

    // 5xx means the server failed, not the request: replaying it would turn a transient fault into a permanent one,
    // so the key is released and a retry executes again. 408/425/429 are "try again" answers by definition, and 409
    // usually reflects state that a later retry may no longer conflict with.
    internal static bool DefaultIsCacheable(int status) => status < 500 && status is not (408 or 409 or 425 or 429);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(HeaderName) || string.IsNullOrWhiteSpace(KeyPrefix))
        {
            throw new InvalidOperationException("Idempotency header name and key prefix must be set.");
        }

        if (MinKeyLength < 1 || MaxKeyLength < MinKeyLength)
        {
            throw new InvalidOperationException("Idempotency key length limits are inconsistent.");
        }

        if (MaxRequestBodyBytes < 0 || MaxResponseBodyBytes < 0)
        {
            throw new InvalidOperationException("Body size limits cannot be negative.");
        }

        DefaultPolicy.Validate();
    }
}
