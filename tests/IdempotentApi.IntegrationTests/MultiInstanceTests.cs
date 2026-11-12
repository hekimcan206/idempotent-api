using System.Net;
using IdempotentApi.IntegrationTests.Infrastructure;
using IdempotentApi.Redis;
using Microsoft.AspNetCore.Builder;
using static IdempotentApi.IntegrationTests.Infrastructure.Requests;

namespace IdempotentApi.IntegrationTests;

/// <summary>
/// The property the library exists for: however many instances receive copies of one request, the side effect
/// happens once. Each instance is a separate host with its own Redis connection.
/// </summary>
public sealed class MultiInstanceTests(RedisFixture redis)
{
    private readonly SharedLedger _ledger = new();

    private async Task<AppInstance[]> StartInstances(int count, Action<IdempotencyPolicy>? policy = null) =>
        await Task.WhenAll(Enumerable.Range(0, count).Select(_ => AppInstance.StartAsync(redis, app =>
            app.MapPost("/payments", (Microsoft.AspNetCore.Http.HttpResponse r) => _ledger.ChargeAsync(r)).RequireIdempotency(policy))));

    private static async Task DisposeAll(IEnumerable<AppInstance> instances)
    {
        foreach (var instance in instances)
        {
            await instance.DisposeAsync();
        }
    }

    [Fact]
    public async Task Hundred_concurrent_copies_across_four_instances_charge_once()
    {
        var instances = await StartInstances(4);
        try
        {
            var key = NewKey();
            var responses = await Task.WhenAll(Enumerable.Range(0, 100).Select(i => instances[i % 4].Client.SendAsync(Post("/payments", key))));

            _ledger.Charges.Should().Be(1);
            responses.Count(r => r.StatusCode == HttpStatusCode.Created && !r.Headers.Contains(IdempotencyMiddleware.ReplayedHeader)).Should().Be(1);
            responses.Select(r => r.StatusCode).Distinct().Should().BeSubsetOf([HttpStatusCode.Created, HttpStatusCode.Conflict]);
            DisposeResponses(responses);
        }
        finally
        {
            await DisposeAll(instances);
        }
    }

    [Fact]
    public async Task Wait_mode_gives_every_copy_the_same_response_from_one_charge()
    {
        var instances = await StartInstances(3, p => p.ConcurrencyMode = ConcurrencyMode.WaitForResult);
        try
        {
            var key = NewKey();
            var responses = await Task.WhenAll(Enumerable.Range(0, 30).Select(i => instances[i % 3].Client.SendAsync(Post("/payments", key))));
            var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));

            _ledger.Charges.Should().Be(1);
            responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created);
            bodies.Distinct().Should().ContainSingle("every copy gets the first execution's body, timestamp included");
            responses.Count(r => r.Headers.Contains(IdempotencyMiddleware.ReplayedHeader)).Should().Be(29);
            DisposeResponses(responses);
        }
        finally
        {
            await DisposeAll(instances);
        }
    }

    [Fact]
    public async Task Many_keys_with_duplicates_charge_once_per_key()
    {
        var instances = await StartInstances(2);
        try
        {
            var keys = Enumerable.Range(0, 40).Select(_ => NewKey()).ToArray();
            var requests = keys.SelectMany(k => Enumerable.Repeat(k, 5)).Select((k, i) => instances[i % 2].Client.SendAsync(Post("/payments", k)));

            var responses = await Task.WhenAll(requests);

            _ledger.Charges.Should().Be(40);
            DisposeResponses(responses);
        }
        finally
        {
            await DisposeAll(instances);
        }
    }

    [Fact]
    public async Task A_crashed_owner_blocks_the_key_only_until_its_lease_lapses()
    {
        var instances = await StartInstances(1);
        try
        {
            // Simulate an instance that claimed the key and died: a claim nobody renews, with a 1 s lease. The storage
            // key is derived exactly as the middleware derives it (anonymous partition, default prefix).
            var key = NewKey();
            IdempotencyKey.TryParse(key, false, 1, 255, out var rawKey, out _);
            var storageKey = IdempotencyMiddleware.StorageKeyFor("idem", "anonymous", rawKey!);
            var fingerprint = new Sha256RequestFingerprinter().Compute(FakeRequest(), System.Text.Encoding.UTF8.GetBytes("{\"amount\":\"25.00\",\"currency\":\"TRY\"}"));
            var dead = (await new RedisIdempotencyStore(redis.Redis).TryBeginAsync(storageKey, fingerprint, TimeSpan.FromSeconds(1), CancellationToken.None)).Lease!;

            using var duringLease = await instances[0].Client.SendAsync(Post("/payments", key));
            await Task.Delay(TimeSpan.FromMilliseconds(1500));
            using var afterLease = await instances[0].Client.SendAsync(Post("/payments", key));
            var lateWrite = await new RedisIdempotencyStore(redis.Redis).CompleteAsync(storageKey, dead.OwnerToken, new StoredResponse(500, [], ReadOnlyMemory<byte>.Empty), TimeSpan.FromMinutes(1), CancellationToken.None);
            using var replay = await instances[0].Client.SendAsync(Post("/payments", key));

            duringLease.StatusCode.Should().Be(HttpStatusCode.Conflict);
            afterLease.StatusCode.Should().Be(HttpStatusCode.Created);
            lateWrite.Should().BeFalse("the dead owner's token no longer matches");
            replay.StatusCode.Should().Be(HttpStatusCode.Created);
            replay.Headers.Contains(IdempotencyMiddleware.ReplayedHeader).Should().BeTrue();
            _ledger.Charges.Should().Be(1);
        }
        finally
        {
            await DisposeAll(instances);
        }
    }

    [Fact]
    public async Task Redis_outage_fails_closed_with_503_and_nothing_is_charged()
    {
        var options = StackExchange.Redis.ConfigurationOptions.Parse("127.0.0.1:1");
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 500;
        options.AsyncTimeout = 500;
        var dead = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(options);
        await using var instance = await AppInstance.StartAsync(redis, app =>
            app.MapPost("/payments", (Microsoft.AspNetCore.Http.HttpResponse r) => _ledger.ChargeAsync(r)).RequireIdempotency(), redis: dead);

        using var response = await instance.Client.SendAsync(Post("/payments", NewKey()));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        _ledger.Charges.Should().Be(0);
    }

    private static Microsoft.AspNetCore.Http.HttpRequest FakeRequest()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/payments";
        context.Request.ContentType = "application/json; charset=utf-8";
        return context.Request;
    }

    private static void DisposeResponses(IEnumerable<HttpResponseMessage> responses)
    {
        foreach (var r in responses)
        {
            r.Dispose();
        }
    }
}
