using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdempotentApi.RateLimiting;

/// <summary>Registered policies and how callers are partitioned.</summary>
public sealed class TokenBucketRateLimitOptions
{
    private readonly Dictionary<string, TokenBucketPolicy> _policies = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, TokenBucketPolicy> Policies => _policies;

    /// <summary>Prefix of every bucket key in the store.</summary>
    public string KeyPrefix { get; set; } = "rl";

    /// <summary>
    /// Who shares a bucket. Defaults to the authenticated user, else the remote IP address. Behind a proxy, configure
    /// forwarded headers first or every client will share the proxy's bucket.
    /// </summary>
    public Func<HttpContext, string> PartitionResolver { get; set; } = DefaultPartition;

    /// <summary>
    /// Admit requests when the store cannot be reached. Rate limiting protects capacity, and refusing all traffic
    /// because the limiter is down turns a partial outage into a full one, so the default is to fail open.
    /// </summary>
    public bool FailOpen { get; set; } = true;

    /// <summary>Emit <c>RateLimit-Policy</c> and <c>RateLimit</c> on every limited response, not only on 429.</summary>
    public bool EmitHeaders { get; set; } = true;

    public Uri ProblemTypeBase { get; set; } = new("https://iana.org/assignments/http-problem-types");

    public TokenBucketRateLimitOptions AddPolicy(string name, int capacity, double refillPerSecond)
    {
        _policies[name] = new TokenBucketPolicy(name, capacity, refillPerSecond);
        return this;
    }

    internal static string DefaultPartition(HttpContext context)
    {
        if (context.User.Identity is { IsAuthenticated: true, Name: { } name })
        {
            return "user:" + name;
        }

        return "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }
}

/// <summary>Endpoint metadata naming the policy (and cost) that applies.</summary>
public sealed record TokenBucketMetadata(string PolicyName, int Cost = 1);

/// <summary>
/// Distributed token-bucket limiting with the IETF RateLimit header fields (draft-ietf-httpapi-ratelimit-headers).
/// ASP.NET Core's built-in rate limiter does not expose remaining quota per request, which those headers need, so this
/// is a small dedicated middleware rather than a <c>System.Threading.RateLimiting.RateLimiter</c> adapter.
/// </summary>
public sealed partial class TokenBucketRateLimitMiddleware(
    RequestDelegate next,
    ITokenBucketStore store,
    IOptions<TokenBucketRateLimitOptions> options,
    ILogger<TokenBucketRateLimitMiddleware> logger)
{
    private readonly TokenBucketRateLimitOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        var metadata = context.GetEndpoint()?.Metadata.GetMetadata<TokenBucketMetadata>();
        if (metadata is null)
        {
            await next(context);
            return;
        }

        if (!_options.Policies.TryGetValue(metadata.PolicyName, out var policy))
        {
            throw new InvalidOperationException($"Rate limit policy '{metadata.PolicyName}' is not registered.");
        }

        if (metadata.Cost > policy.Capacity || metadata.Cost < 1)
        {
            throw new InvalidOperationException($"Cost {metadata.Cost} can never be satisfied by policy '{policy.Name}' (capacity {policy.Capacity}).");
        }

        var bucketKey = $"{_options.KeyPrefix}:{policy.Name}:{_options.PartitionResolver(context)}";
        TokenBucketDecision decision;
        try
        {
            decision = await store.TakeAsync(bucketKey, policy, metadata.Cost, context.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
        {
            LogStoreUnavailable(logger, ex, policy.Name, _options.FailOpen);
            IdempotencyTelemetry.RecordRateLimit(policy.Name, _options.FailOpen ? "fail_open" : "fail_closed");
            if (_options.FailOpen)
            {
                await next(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "1";
            return;
        }

        if (_options.EmitHeaders || !decision.Allowed)
        {
            var headers = context.Response.Headers;
            headers["RateLimit-Policy"] = RateLimitHeaders.Policy(policy);
            headers["RateLimit"] = RateLimitHeaders.Limit(policy, decision);
        }

        if (decision.Allowed)
        {
            IdempotencyTelemetry.RecordRateLimit(policy.Name, "allowed");
            await next(context);
            return;
        }

        IdempotencyTelemetry.RecordRateLimit(policy.Name, "denied");
        context.Response.Headers.RetryAfter = RateLimitHeaders.Seconds(decision.RetryAfter).ToString(CultureInfo.InvariantCulture);
        await Problems.WriteAsync(context, _options.ProblemTypeBase, "quota-exceeded", StatusCodes.Status429TooManyRequests,
            "Too Many Requests",
            $"Quota of policy '{policy.Name}' exhausted; retry after the Retry-After interval.",
            w =>
            {
                w.WriteStartArray("violated-policies");
                w.WriteStringValue(policy.Name);
                w.WriteEndArray();
            });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Rate limit store unavailable for policy {Policy}; fail open: {FailOpen}")]
    private static partial void LogStoreUnavailable(ILogger logger, Exception exception, string policy, bool failOpen);
}

/// <summary>Field values per draft-ietf-httpapi-ratelimit-headers-11 (Structured Field Lists).</summary>
public static class RateLimitHeaders
{
    /// <summary><c>"name";q=capacity;w=seconds-to-refill-from-empty</c>.</summary>
    public static string Policy(TokenBucketPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return string.Create(CultureInfo.InvariantCulture, $"{IdempotencyKey.Format(policy.Name)};q={policy.Capacity};w={policy.WindowSeconds}");
    }

    /// <summary>
    /// <c>"name";r=remaining;t=seconds</c>. For a token bucket <c>t</c> is the time until the bucket is full again: a
    /// client that spends no more than <c>r</c> before then cannot be throttled by this policy.
    /// </summary>
    public static string Limit(TokenBucketPolicy policy, TokenBucketDecision decision)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return string.Create(CultureInfo.InvariantCulture, $"{IdempotencyKey.Format(policy.Name)};r={Math.Max(0, decision.Remaining)};t={Seconds(decision.ResetAfter)}");
    }

    // Both fields carry whole seconds. Rounding up never tells a client to come back too early.
    internal static long Seconds(TimeSpan span) => Math.Max(span > TimeSpan.Zero ? 1 : 0, (long)Math.Ceiling(span.TotalSeconds));
}
