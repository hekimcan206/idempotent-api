using IdempotentApi.Redis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace IdempotentApi.IntegrationTests.Infrastructure;

/// <summary>
/// One "application instance": its own host, DI container and Redis connection, so several of them in one test
/// behave like a horizontally scaled deployment sharing a Redis.
/// </summary>
public sealed class AppInstance : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly IConnectionMultiplexer _redis;

    private AppInstance(WebApplication app, IConnectionMultiplexer redis)
    {
        _app = app;
        _redis = redis;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    public static async Task<AppInstance> StartAsync(
        RedisFixture fixture,
        Action<WebApplication> map,
        Action<IdempotencyOptions>? idempotency = null,
        Action<RateLimiting.TokenBucketRateLimitOptions>? rateLimits = null,
        IConnectionMultiplexer? redis = null)
    {
        redis ??= await fixture.Connect();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(redis);
        builder.Services.AddIdempotency(o =>
        {
            o.MinKeyLength = 8;
            idempotency?.Invoke(o);
        });
        builder.Services.AddRedisIdempotencyStore("unused:6379");
        builder.Services.AddTokenBucketRateLimiting(o => rateLimits?.Invoke(o));
        builder.Services.AddRedisTokenBuckets("unused:6379");

        var app = builder.Build();
        app.UseRouting();
        app.UseTokenBucketRateLimiting();
        app.UseIdempotency();
        map(app);
        await app.StartAsync();
        return new AppInstance(app, redis);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
        await _redis.DisposeAsync();
    }
}

/// <summary>A side effect that is counted outside any one instance, like a payment provider would count charges.</summary>
public sealed class SharedLedger
{
    private int _charges;

    public int Charges => Volatile.Read(ref _charges);

    public async Task<IResult> ChargeAsync(HttpResponse response, int delayMs = 30)
    {
        var n = Interlocked.Increment(ref _charges);
        await Task.Delay(delayMs);
        response.Headers["X-Charge"] = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Created($"/payments/{n}", new { charge = n, at = DateTimeOffset.UtcNow });
    }
}

internal static class Requests
{
    public static HttpRequestMessage Post(string path, string key, string body = "{\"amount\":\"25.00\",\"currency\":\"TRY\"}")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return request;
    }

    public static string NewKey() => IdempotencyKey.Format(Guid.NewGuid().ToString());
}
