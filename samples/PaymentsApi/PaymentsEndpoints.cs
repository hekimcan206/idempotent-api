using System.Globalization;
using IdempotentApi;
using StackExchange.Redis;

namespace PaymentsApi;

public sealed class PaymentsSettings
{
    public string InstanceName { get; set; } = Environment.MachineName;

    /// <summary>Simulated provider latency. It widens the race window, which is what the demo wants to show.</summary>
    public int ChargeDelayMs { get; set; } = 50;

    public int RateLimitCapacity { get; set; } = 100;

    public double RateLimitRefillPerSecond { get; set; } = 20;
}

/// <summary>A synthetic charge request. Amounts are decimal strings, as money should be on the wire.</summary>
public sealed record ChargeRequest(string Amount, string Currency, string MerchantReference);

public sealed record ChargeResponse(
    string PaymentId,
    string Amount,
    string Currency,
    string MerchantReference,
    string ChargedBy,
    long FencingToken,
    DateTimeOffset ChargedAt);

public sealed record LedgerStats(long TotalCharges, IReadOnlyDictionary<string, long> ChargesByReference);

/// <summary>
/// Stands in for a payment provider: every call is a real, counted side effect in Redis, so the demo can prove how
/// many times a charge actually ran, independent of which instance ran it.
/// </summary>
public sealed class PaymentLedger(IConnectionMultiplexer redis, PaymentsSettings settings)
{
    private const string TotalKey = "demo:charges:total";
    private const string ByReferenceKey = "demo:charges:by-reference";

    public async Task<ChargeResponse> ChargeAsync(ChargeRequest request, long fencingToken, CancellationToken cancellationToken)
    {
        await Task.Delay(settings.ChargeDelayMs, cancellationToken);
        var db = redis.GetDatabase();
        await db.StringIncrementAsync(TotalKey);
        await db.HashIncrementAsync(ByReferenceKey, request.MerchantReference);
        return new ChargeResponse(
            "pay_" + Guid.NewGuid().ToString("N")[..20],
            request.Amount,
            request.Currency,
            request.MerchantReference,
            settings.InstanceName,
            fencingToken,
            DateTimeOffset.UtcNow);
    }

    public async Task<LedgerStats> StatsAsync()
    {
        var db = redis.GetDatabase();
        var total = (long?)await db.StringGetAsync(TotalKey) ?? 0;
        var byReference = (await db.HashGetAllAsync(ByReferenceKey)).ToDictionary(e => e.Name.ToString(), e => (long)e.Value);
        return new LedgerStats(total, byReference);
    }
}

public static class PaymentsEndpoints
{
    public const string ClientIdHeader = "X-Client-Id";

    public static string ClientId(HttpContext context)
    {
        var id = context.Request.Headers[ClientIdHeader].ToString();
        // A real API would take this from an authenticated API key; the header keeps the demo self-contained.
        return id.Length is > 0 and <= 64 ? "client:" + id : "ip:" + context.Connection.RemoteIpAddress;
    }

    public static IEndpointRouteBuilder MapPaymentsEndpoints(this IEndpointRouteBuilder app)
    {
        // Reject mode: the draft's behaviour. A duplicate that arrives mid-flight gets 409 and retries later.
        app.MapPost("/payments", Charge)
            .RequireIdempotency()
            .RequireTokenBucket("payments")
            .WithSummary("Charge a synthetic payment. Requires Idempotency-Key; concurrent duplicates get 409.");

        // Wait mode: duplicates are held until the first execution finishes and then receive its response.
        app.MapPost("/transfers", Charge)
            .RequireIdempotency(p => p.ConcurrencyMode = ConcurrencyMode.WaitForResult)
            .RequireTokenBucket("payments")
            .WithSummary("Same charge, but concurrent duplicates wait for and replay the first response.");

        app.MapGet("/ledger", async (PaymentLedger ledger) => Results.Ok(await ledger.StatsAsync()))
            .WithSummary("How many charges really ran, in total and per merchant reference.");

        return app;
    }

    private static async Task<IResult> Charge(ChargeRequest request, HttpContext context, PaymentLedger ledger)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var fence = context.Features.Get<IIdempotencyFeature>()?.FencingToken ?? 0;
        var charge = await ledger.ChargeAsync(request, fence, context.RequestAborted);
        return Results.Created($"/payments/{charge.PaymentId}", charge);
    }

    private static Dictionary<string, string[]> Validate(ChargeRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (!decimal.TryParse(request.Amount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) || amount <= 0 || decimal.Round(amount, 2) != amount)
        {
            errors["amount"] = ["Amount must be a positive decimal string with at most two decimals, e.g. \"125.50\"."];
        }

        if (request.Currency is not { Length: 3 } currency || !currency.All(char.IsAsciiLetterUpper))
        {
            errors["currency"] = ["Currency must be an ISO 4217 code such as TRY or EUR."];
        }

        if (string.IsNullOrWhiteSpace(request.MerchantReference) || request.MerchantReference.Length > 64)
        {
            errors["merchantReference"] = ["Merchant reference is required (at most 64 characters)."];
        }

        return errors;
    }
}
