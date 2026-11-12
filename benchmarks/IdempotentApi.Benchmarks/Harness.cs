using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IdempotentApi.Benchmarks;

/// <summary>A real Kestrel instance on a loopback port, so latency includes HTTP parsing and the socket.</summary>
internal sealed class KestrelInstance : IAsyncDisposable
{
    private readonly WebApplication _app;

    private KestrelInstance(WebApplication app, Uri address)
    {
        _app = app;
        Client = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 256, PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
        {
            BaseAddress = address,
            Timeout = TimeSpan.FromSeconds(60),
        };
    }

    public HttpClient Client { get; }

    public static async Task<KestrelInstance> StartAsync(IIdempotencyStore? store, Action<IdempotencyPolicy>? policy, Func<HttpContext, Task<IResult>> handler)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        if (store is not null)
        {
            builder.Services.AddSingleton(store);
            builder.Services.AddIdempotency(o => o.MinKeyLength = 8);
        }

        var app = builder.Build();
        app.UseRouting();
        if (store is not null)
        {
            app.UseIdempotency();
        }

        var endpoint = app.MapPost("/op", handler);
        if (store is not null)
        {
            endpoint.RequireIdempotency(policy);
        }

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new KestrelInstance(app, new Uri(address));
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }
}

internal static class Http
{
    public static HttpRequestMessage Post(string? key, string body = "{\"amount\":\"125.50\",\"currency\":\"TRY\"}")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/op") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return request;
    }

    public static string NewKey() => IdempotencyKey.Format(Guid.NewGuid().ToString());
}

internal static class Stats
{
    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0)
        {
            return double.NaN;
        }

        var rank = p / 100d * (sorted.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return sorted[lo] + ((sorted[hi] - sorted[lo]) * (rank - lo));
    }

    public static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return Percentile(sorted, 50);
    }

    public static string F(double value, int decimals = 2) => value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}

internal static class Machine
{
    public static Dictionary<string, string> Describe()
    {
        return new Dictionary<string, string>
        {
            ["os"] = RuntimeInformation.OSDescription,
            ["arch"] = RuntimeInformation.OSArchitecture.ToString(),
            ["logicalCores"] = Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture),
            ["dotnet"] = RuntimeInformation.FrameworkDescription,
            ["cpu"] = Environment.GetEnvironmentVariable("BENCH_CPU") ?? "unknown",
            ["hostLoad"] = Environment.GetEnvironmentVariable("BENCH_HOST_LOAD") ?? "unknown",
            ["redis"] = "redis:7-alpine in Docker Desktop (Testcontainers), loopback TCP",
            ["timestampUtc"] = DateTimeOffset.UtcNow.ToString("u", CultureInfo.InvariantCulture),
        };
    }
}

/// <summary>Collects latencies of a closed-loop load run.</summary>
internal static class Load
{
    public sealed record Result(int Requests, int Concurrency, double Seconds, double Rps, double P50, double P95, double P99, Dictionary<int, int> Statuses);

    public static async Task<Result> RunAsync(int requests, int concurrency, Func<int, Task<int>> send)
    {
        var latencies = new double[requests];
        var statuses = new int[requests];
        var next = -1;
        var stopwatch = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
        {
            int i;
            while ((i = Interlocked.Increment(ref next)) < requests)
            {
                var start = Stopwatch.GetTimestamp();
                statuses[i] = await send(i);
                latencies[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
        }));
        var seconds = stopwatch.Elapsed.TotalSeconds;
        Array.Sort(latencies);
        return new Result(
            requests,
            concurrency,
            seconds,
            requests / seconds,
            Stats.Percentile(latencies, 50),
            Stats.Percentile(latencies, 95),
            Stats.Percentile(latencies, 99),
            statuses.GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count()));
    }
}
