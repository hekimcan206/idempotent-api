using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace IdempotentApi;

/// <summary>
/// Meter and activity source names to register with OpenTelemetry
/// (<c>.AddMeter(IdempotencyTelemetry.Name)</c>, <c>.AddSource(IdempotencyTelemetry.Name)</c>).
/// </summary>
public static class IdempotencyTelemetry
{
    public const string Name = "IdempotentApi";

    internal static readonly ActivitySource ActivitySource = new(Name);

    private static readonly Meter Meter = new(Name);

    /// <summary>One increment per protected request, tagged with how it was answered.</summary>
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>(
        "idempotency.requests", unit: "{request}", description: "Idempotent requests by outcome.");

    private static readonly Histogram<double> WaitDuration = Meter.CreateHistogram<double>(
        "idempotency.wait.duration", unit: "s", description: "Time duplicates spent waiting for the first execution.");

    private static readonly Counter<long> RateLimitDecisions = Meter.CreateCounter<long>(
        "ratelimit.decisions", unit: "{request}", description: "Token bucket decisions by policy and result.");

    internal static void Record(string outcome)
    {
        Requests.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        Activity.Current?.SetTag("idempotency.outcome", outcome);
    }

    internal static void RecordWait(TimeSpan elapsed) => WaitDuration.Record(elapsed.TotalSeconds);

    internal static void RecordRateLimit(string policy, string result) =>
        RateLimitDecisions.Add(1, new KeyValuePair<string, object?>("policy", policy), new KeyValuePair<string, object?>("result", result));
}

/// <summary>Values of the <c>outcome</c> tag on <c>idempotency.requests</c>.</summary>
public static class IdempotencyOutcomes
{
    public const string Executed = "executed";
    public const string Replayed = "replayed";
    public const string Released = "released";
    public const string Conflict = "conflict";
    public const string Mismatch = "mismatch";
    public const string MissingKey = "missing_key";
    public const string InvalidKey = "invalid_key";
    public const string BodyTooLarge = "body_too_large";
    public const string NotReplayable = "not_replayable";
    public const string StoreUnavailable = "store_unavailable";
    public const string LeaseLost = "lease_lost";
    public const string Unprotected = "unprotected";
}
