using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace DemoDriver;

/// <summary>One scenario's verdict.</summary>
public sealed record ScenarioResult(string Name, bool Passed, string Summary);

/// <summary>
/// The compose demo, as code. Every scenario sends real HTTP requests to two API instances that share one Redis and
/// checks the outcome against the payments ledger (the count of charges that actually ran). The integration tests
/// run this same class against in-process instances.
/// </summary>
public sealed class DemoScenarios(IReadOnlyList<HttpClient> instances, TextWriter output)
{
    private readonly string _runId = Guid.NewGuid().ToString("N")[..8];

    public async Task<IReadOnlyList<ScenarioResult>> RunAllAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<ScenarioResult>
        {
            await ConcurrentDuplicatesAsync(cancellationToken),
            await RetryAfterCompletionAsync(cancellationToken),
            await KeyReusedWithAnotherBodyAsync(cancellationToken),
            await MissingKeyAsync(cancellationToken),
            await WaitModeAsync(cancellationToken),
            await SharedRateLimitAsync(cancellationToken),
        };

        foreach (var r in results)
        {
            output.WriteLine($"{(r.Passed ? "PASS" : "FAIL")}  {r.Name,-28} {r.Summary}");
        }

        var summary = new
        {
            scenarios = results.Count,
            passed = results.Count(r => r.Passed),
            failures = results.Where(r => !r.Passed).Select(r => r.Name).ToArray(),
        };
        output.WriteLine(JsonSerializer.Serialize(summary));
        return results;
    }

    private string Reference(string scenario) => $"demo-{_runId}-{scenario}";

    private HttpClient Instance(int i) => instances[i % instances.Count];

    private static HttpRequestMessage Charge(string path, string clientId, string? key, string reference, string amount = "125.50")
    {
        var body = $$"""{"amount":"{{amount}}","currency":"TRY","merchantReference":"{{reference}}"}""";
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Client-Id", clientId);
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", $"\"{key}\"");
        }

        return request;
    }

    private async Task<long> ChargesFor(string reference, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(await Instance(0).GetStringAsync(new Uri("/ledger", UriKind.Relative), cancellationToken));
        return doc.RootElement.GetProperty("chargesByReference").TryGetProperty(reference, out var n) ? n.GetInt64() : 0;
    }

    private static bool IsReplay(HttpResponseMessage r) => r.Headers.TryGetValues("Idempotent-Replayed", out var v) && v.Contains("true");

    // 1. Fifty copies of one payment, fired at both instances at once: exactly one charge.
    private async Task<ScenarioResult> ConcurrentDuplicatesAsync(CancellationToken ct)
    {
        const int copies = 50;
        var reference = Reference("dup");
        var client = "client-" + _runId + "-dup";
        var key = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(Enumerable.Range(0, copies).Select(i => Instance(i).SendAsync(Charge("/payments", client, key, reference), ct)));
        var fresh = responses.Count(r => r.StatusCode == HttpStatusCode.Created && !IsReplay(r));
        var replays = responses.Count(r => r.StatusCode == HttpStatusCode.Created && IsReplay(r));
        var conflicts = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);
        var charges = await ChargesFor(reference, ct);
        Dispose(responses);

        var passed = fresh == 1 && charges == 1 && fresh + replays + conflicts == copies;
        return new("concurrent-duplicates", passed,
            $"{copies} copies over {instances.Count} instances -> 201 x{fresh}, replayed x{replays}, 409 x{conflicts}; charges run: {charges}");
    }

    // 2. A retry after the first finished gets the same response back, marked as a replay, and nothing runs again.
    private async Task<ScenarioResult> RetryAfterCompletionAsync(CancellationToken ct)
    {
        var reference = Reference("retry");
        var client = "client-" + _runId + "-retry";
        var key = Guid.NewGuid().ToString();

        using var first = await Instance(0).SendAsync(Charge("/payments", client, key, reference), ct);
        using var retry = await Instance(1).SendAsync(Charge("/payments", client, key, reference), ct);
        var firstBody = await first.Content.ReadAsStringAsync(ct);
        var retryBody = await retry.Content.ReadAsStringAsync(ct);
        var charges = await ChargesFor(reference, ct);

        var passed = first.StatusCode == HttpStatusCode.Created && retry.StatusCode == HttpStatusCode.Created
            && IsReplay(retry) && firstBody == retryBody && charges == 1;
        return new("retry-after-completion", passed,
            $"first 201 on instance A, retry on instance B -> {(int)retry.StatusCode} replayed={IsReplay(retry)}, identical body={firstBody == retryBody}; charges run: {charges}");
    }

    // 3. Reusing a key for a different payment is refused with 422.
    private async Task<ScenarioResult> KeyReusedWithAnotherBodyAsync(CancellationToken ct)
    {
        var reference = Reference("reuse");
        var client = "client-" + _runId + "-reuse";
        var key = Guid.NewGuid().ToString();

        using var first = await Instance(0).SendAsync(Charge("/payments", client, key, reference, "10.00"), ct);
        using var reused = await Instance(1).SendAsync(Charge("/payments", client, key, reference, "9999.00"), ct);
        var charges = await ChargesFor(reference, ct);

        var passed = reused.StatusCode == HttpStatusCode.UnprocessableEntity && charges == 1;
        return new("key-reused-other-body", passed, $"same key, amount 10.00 then 9999.00 -> {(int)reused.StatusCode}; charges run: {charges}");
    }

    // 4. A payment without a key is refused with 400 before anything runs.
    private async Task<ScenarioResult> MissingKeyAsync(CancellationToken ct)
    {
        var reference = Reference("nokey");
        using var response = await Instance(0).SendAsync(Charge("/payments", "client-" + _runId + "-nokey", null, reference), ct);
        var charges = await ChargesFor(reference, ct);

        var passed = response.StatusCode == HttpStatusCode.BadRequest && charges == 0;
        return new("missing-key", passed, $"no Idempotency-Key -> {(int)response.StatusCode}; charges run: {charges}");
    }

    // 5. Wait mode: twenty copies all receive the same 201 body, one charge.
    private async Task<ScenarioResult> WaitModeAsync(CancellationToken ct)
    {
        const int copies = 20;
        var reference = Reference("wait");
        var client = "client-" + _runId + "-wait";
        var key = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(Enumerable.Range(0, copies).Select(i => Instance(i).SendAsync(Charge("/transfers", client, key, reference), ct)));
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync(ct)));
        var created = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var charges = await ChargesFor(reference, ct);
        Dispose(responses);

        var distinct = bodies.Distinct().Count();
        var passed = created == copies && distinct == 1 && charges == 1;
        return new("wait-mode", passed, $"{copies} copies -> 201 x{created}, distinct bodies: {distinct}; charges run: {charges}");
    }

    // 6. One client's token bucket is shared by both instances.
    private async Task<ScenarioResult> SharedRateLimitAsync(CancellationToken ct)
    {
        const int requests = 160;
        var client = "client-" + _runId + "-burst";
        var stopwatch = Stopwatch.StartNew();
        var responses = await Task.WhenAll(Enumerable.Range(0, requests).Select(i =>
            Instance(i).SendAsync(Charge("/payments", client, Guid.NewGuid().ToString(), Reference("burst") + "-" + i.ToString(CultureInfo.InvariantCulture)), ct)));
        var elapsed = stopwatch.Elapsed.TotalSeconds;

        var allowed = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var limited = responses.Where(r => r.StatusCode == HttpStatusCode.TooManyRequests).ToList();
        var headersOk = limited.All(r => r.Headers.RetryAfter is not null && r.Headers.Contains("RateLimit") && r.Headers.Contains("RateLimit-Policy"));
        var policy = responses.First().Headers.TryGetValues("RateLimit-Policy", out var p) ? p.First() : "?";
        Dispose(responses);

        // The sample policy is capacity 100, refill 20/s (see appsettings.json).
        var bound = 100 + (20 * elapsed);
        var passed = allowed >= 100 && allowed <= Math.Floor(bound) && limited.Count == requests - allowed && headersOk;
        return new("shared-rate-limit", passed, string.Create(CultureInfo.InvariantCulture,
            $"{requests} requests in {elapsed:F2} s over {instances.Count} instances -> allowed {allowed} (bound {bound:F0}), 429 x{limited.Count} with Retry-After; policy {policy}"));
    }

    private static void Dispose(IEnumerable<HttpResponseMessage> responses)
    {
        foreach (var r in responses)
        {
            r.Dispose();
        }
    }
}
