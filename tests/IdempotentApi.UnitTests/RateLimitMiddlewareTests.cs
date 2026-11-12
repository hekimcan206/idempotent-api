using System.Net;
using IdempotentApi.RateLimiting;
using IdempotentApi.UnitTests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IdempotentApi.UnitTests;

public sealed class RateLimitMiddlewareTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));

    private Task<TestApp> StartAsync(Action<TokenBucketRateLimitOptions>? configure = null, ITokenBucketStore? store = null, int cost = 1) =>
        TestApp.StartAsync(
            app =>
            {
                app.MapPost("/limited", () => Results.Ok()).RequireTokenBucket("burst", cost);
                app.MapPost("/free", () => Results.Ok());
                app.MapPost("/unknown", () => Results.Ok()).RequireTokenBucket("nope");
            },
            services =>
            {
                services.AddSingleton<TimeProvider>(_time);
                if (store is not null)
                {
                    services.AddSingleton(store);
                }

                services.AddTokenBucketRateLimiting(o =>
                {
                    o.AddPolicy("burst", capacity: 5, refillPerSecond: 1);
                    o.PartitionResolver = c => c.Request.Headers["X-Client"].ToString();
                    configure?.Invoke(o);
                });
            },
            pipeline => pipeline.UseTokenBucketRateLimiting());

    private static HttpRequestMessage Request(string client = "c1", string path = "/limited")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-Client", client);
        return request;
    }

    [Fact]
    public async Task Burst_up_to_capacity_then_429_with_retry_after_and_headers()
    {
        await using var app = await StartAsync();

        for (var i = 0; i < 5; i++)
        {
            using var ok = await app.Client.SendAsync(Request());
            ok.StatusCode.Should().Be(HttpStatusCode.OK);
            ok.Headers.GetValues("RateLimit").Single().Should().Be($"\"burst\";r={4 - i};t={i + 1}");
            ok.Headers.GetValues("RateLimit-Policy").Single().Should().Be("\"burst\";q=5;w=5");
        }

        using var denied = await app.Client.SendAsync(Request());
        denied.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        denied.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(1));
        Golden.Verify("rate-limited.txt", $"{denied.Headers.GetValues("RateLimit").Single()}\n{await denied.Content.ReadAsStringAsync()}\n");
    }

    [Fact]
    public async Task Refills_with_time()
    {
        await using var app = await StartAsync();
        for (var i = 0; i < 5; i++)
        {
            (await app.Client.SendAsync(Request())).Dispose();
        }

        _time.Advance(TimeSpan.FromMilliseconds(999));
        using var early = await app.Client.SendAsync(Request());
        _time.Advance(TimeSpan.FromMilliseconds(1));
        using var onTime = await app.Client.SendAsync(Request());

        early.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        onTime.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Partitions_have_separate_buckets()
    {
        await using var app = await StartAsync();
        for (var i = 0; i < 5; i++)
        {
            (await app.Client.SendAsync(Request("alice"))).Dispose();
        }

        using var alice = await app.Client.SendAsync(Request("alice"));
        using var bob = await app.Client.SendAsync(Request("bob"));

        alice.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        bob.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Cost_spends_several_tokens()
    {
        await using var app = await StartAsync(cost: 3);

        using var first = await app.Client.SendAsync(Request());
        using var second = await app.Client.SendAsync(Request());

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        second.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Unlimited_endpoints_have_no_headers()
    {
        await using var app = await StartAsync();
        using var response = await app.Client.SendAsync(Request(path: "/free"));
        response.Headers.Contains("RateLimit").Should().BeFalse();
    }

    [Fact]
    public async Task Headers_can_be_limited_to_429()
    {
        await using var app = await StartAsync(o => o.EmitHeaders = false);
        using var response = await app.Client.SendAsync(Request());
        response.Headers.Contains("RateLimit").Should().BeFalse();
    }

    [Fact]
    public async Task Unregistered_policy_is_a_configuration_error()
    {
        await using var app = await StartAsync();
        var act = () => app.Client.SendAsync(Request(path: "/unknown"));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*'nope' is not registered*");
    }

    [Fact]
    public async Task Store_outage_fails_open_by_default()
    {
        await using var app = await StartAsync(store: new ThrowingBuckets());
        using var response = await app.Client.SendAsync(Request());
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Store_outage_can_fail_closed()
    {
        await using var app = await StartAsync(o => o.FailOpen = false, new ThrowingBuckets());
        using var response = await app.Client.SendAsync(Request());
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public void Header_seconds_round_up_and_never_say_zero_for_a_positive_wait()
    {
        RateLimitHeaders.Seconds(TimeSpan.Zero).Should().Be(0);
        RateLimitHeaders.Seconds(TimeSpan.FromMilliseconds(1)).Should().Be(1);
        RateLimitHeaders.Seconds(TimeSpan.FromMilliseconds(1001)).Should().Be(2);
    }

    private sealed class ThrowingBuckets : ITokenBucketStore
    {
        public ValueTask<TokenBucketDecision> TakeAsync(string bucketKey, TokenBucketPolicy policy, int cost, CancellationToken cancellationToken) =>
            throw new TimeoutException("redis down");
    }
}
