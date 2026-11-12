using System.Net;
using IdempotentApi.UnitTests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using static IdempotentApi.UnitTests.Support.RequestExtensions;

namespace IdempotentApi.UnitTests;

public sealed class LeaseAndConcurrencyTests
{
    [Fact]
    public async Task Lease_is_renewed_while_a_slow_handler_runs()
    {
        // Lease 300 ms, handler 1.2 s: without renewal the key would lapse after 300 ms and the duplicate would run.
        var calls = 0;
        var entered = new TaskCompletionSource();
        await using var app = await TestApp.StartAsync(a => a.MapPost("/slow", async () =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await Task.Delay(1200);
            return Results.Ok();
        }).RequireIdempotency(p => p.Lease = TimeSpan.FromMilliseconds(300)));
        var key = NewKey();

        var first = app.Client.SendAsync(Post("/slow", key));
        await entered.Task;
        await Task.Delay(800);
        using var duplicate = await app.Client.SendAsync(Post("/slow", key));
        using var firstResponse = await first;

        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task Lost_lease_cancels_the_feature_token_and_the_late_result_is_not_stored()
    {
        var store = new LeaseLosingStore();
        var observedLoss = false;
        await using var app = await TestApp.StartAsync(
            a => a.MapPost("/slow", async (HttpContext c) =>
            {
                var feature = c.Features.Get<IIdempotencyFeature>()!;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), feature.LeaseLost);
                }
                catch (OperationCanceledException)
                {
                    observedLoss = true;
                }

                return Results.Ok();
            }).RequireIdempotency(p => p.Lease = TimeSpan.FromMilliseconds(300)),
            services => services.AddSingleton<IIdempotencyStore>(store));

        using var response = await app.Client.SendAsync(Post("/slow", NewKey()));

        observedLoss.Should().BeTrue();
        store.CompleteCalls.Should().Be(1);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the client still gets the answer; only the stored copy is refused");
    }

    [Fact]
    public async Task Two_hundred_concurrent_requests_with_one_key_execute_once()
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(a => a.MapPost("/payments", async () =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(50);
            return Results.Created("/payments/1", new { id = 1 });
        }).RequireIdempotency());
        var key = NewKey();

        var responses = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => app.Client.SendAsync(Post("/payments", key))));

        calls.Should().Be(1);
        var statuses = responses.GroupBy(r => r.StatusCode).ToDictionary(g => g.Key, g => g.Count());
        statuses.Keys.Should().BeSubsetOf([HttpStatusCode.Created, HttpStatusCode.Conflict]);
        responses.Count(r => r.StatusCode == HttpStatusCode.Created && !r.Headers.Contains(IdempotencyMiddleware.ReplayedHeader)).Should().Be(1);
        foreach (var r in responses)
        {
            r.Dispose();
        }
    }

    [Fact]
    public async Task Distinct_keys_do_not_block_each_other()
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(a => a.MapPost("/payments", async () =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(20);
            return Results.Ok();
        }).RequireIdempotency());
        var keys = Enumerable.Range(0, 50).Select(_ => NewKey()).ToArray();

        var responses = await Task.WhenAll(keys.SelectMany(k => Enumerable.Repeat(k, 4)).Select(k => app.Client.SendAsync(Post("/payments", k))));

        calls.Should().Be(50);
        responses.Count(r => r.StatusCode == HttpStatusCode.OK && !r.Headers.Contains(IdempotencyMiddleware.ReplayedHeader)).Should().Be(50);
        foreach (var r in responses)
        {
            r.Dispose();
        }
    }

    /// <summary>Grants a claim but refuses every renewal, as if another instance took the key over.</summary>
    private sealed class LeaseLosingStore : IIdempotencyStore
    {
        public int CompleteCalls;

        public ValueTask<BeginResult> TryBeginAsync(string storageKey, string fingerprint, TimeSpan lease, CancellationToken cancellationToken) =>
            ValueTask.FromResult(BeginResult.Acquired(new IdempotencyLease("owner", 1)));

        public ValueTask<bool> RenewAsync(string storageKey, string ownerToken, TimeSpan lease, CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);

        public ValueTask<bool> CompleteAsync(string storageKey, string ownerToken, StoredResponse response, TimeSpan retention, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CompleteCalls);
            return ValueTask.FromResult(false);
        }

        public ValueTask<bool> ReleaseAsync(string storageKey, string ownerToken, CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }
}
