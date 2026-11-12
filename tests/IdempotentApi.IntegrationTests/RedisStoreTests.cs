using IdempotentApi.ContractTests;
using IdempotentApi.IntegrationTests.Infrastructure;
using IdempotentApi.Redis;
using StackExchange.Redis;

namespace IdempotentApi.IntegrationTests;

/// <summary>The shared store contract, on a real Redis 7.</summary>
public sealed class RedisStoreContractTests(RedisFixture redis) : IdempotencyStoreContract
{
    protected override IIdempotencyStore Store { get; } = new RedisIdempotencyStore(redis.Redis);
}

/// <summary>Redis-specific behaviour: TTLs, layout and cross-connection atomicity.</summary>
public sealed class RedisStoreTests(RedisFixture redis)
{
    private readonly RedisIdempotencyStore _store = new(redis.Redis);
    private IDatabase Db => redis.Redis.GetDatabase();

    [Fact]
    public async Task Ttl_is_the_lease_while_running_and_the_retention_once_done()
    {
        var key = "ttl:" + Guid.NewGuid().ToString("N");
        var lease = (await _store.TryBeginAsync(key, "fp", TimeSpan.FromSeconds(30), CancellationToken.None)).Lease!;

        var runningTtl = await Db.KeyTimeToLiveAsync(key);
        runningTtl.Should().BeCloseTo(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2));

        await _store.CompleteAsync(key, lease.OwnerToken, new StoredResponse(200, [], "ok"u8.ToArray()), TimeSpan.FromHours(24), CancellationToken.None);
        var doneTtl = await Db.KeyTimeToLiveAsync(key);
        doneTtl.Should().BeCloseTo(TimeSpan.FromHours(24), TimeSpan.FromSeconds(5));
        (await Db.HashGetAsync(key, "owner")).IsNull.Should().BeTrue("the owner token is dropped once the outcome is final");
    }

    [Fact]
    public async Task Renewal_extends_the_ttl()
    {
        var key = "renew:" + Guid.NewGuid().ToString("N");
        var lease = (await _store.TryBeginAsync(key, "fp", TimeSpan.FromSeconds(2), CancellationToken.None)).Lease!;

        (await _store.RenewAsync(key, lease.OwnerToken, TimeSpan.FromSeconds(60), CancellationToken.None)).Should().BeTrue();

        (await Db.KeyTimeToLiveAsync(key)).Should().BeGreaterThan(TimeSpan.FromSeconds(50));
    }

    [Fact]
    public async Task Fencing_tokens_increase_strictly_across_keys_and_connections()
    {
        await using var other = await redis.Connect();
        var otherStore = new RedisIdempotencyStore(other);
        var tokens = new List<long>();
        for (var i = 0; i < 20; i++)
        {
            var store = i % 2 == 0 ? _store : otherStore;
            tokens.Add((await store.TryBeginAsync("fence:" + Guid.NewGuid().ToString("N"), "fp", TimeSpan.FromSeconds(5), CancellationToken.None)).Lease!.FencingToken);
        }

        tokens.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Claims_from_eight_connections_at_once_grant_exactly_one_lease()
    {
        var connections = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => redis.Connect()));
        try
        {
            var key = "race:" + Guid.NewGuid().ToString("N");
            var claims = await Task.WhenAll(connections.SelectMany(c => Enumerable.Range(0, 25).Select(_ =>
                new RedisIdempotencyStore(c).TryBeginAsync(key, "fp", TimeSpan.FromSeconds(30), CancellationToken.None).AsTask())));

            claims.Count(c => c.Outcome == BeginOutcome.Acquired).Should().Be(1);
            claims.Count(c => c.Outcome == BeginOutcome.InFlight).Should().Be(199);
        }
        finally
        {
            foreach (var c in connections)
            {
                await c.DisposeAsync();
            }
        }
    }

    [Fact]
    public void Header_codec_round_trips_and_drops_values_with_line_breaks()
    {
        List<KeyValuePair<string, string>> headers = [new("Location", "/a"), new("X-Empty", string.Empty), new("X-Bad", "a\r\nInjected: 1"), new("Vary", "Accept")];

        var decoded = RedisIdempotencyStore.DecodeHeaders(RedisIdempotencyStore.EncodeHeaders(headers));

        decoded.Should().Equal(new KeyValuePair<string, string>[] { new("Location", "/a"), new("X-Empty", string.Empty), new("Vary", "Accept") });
    }

    [Fact]
    public async Task Unavailable_redis_surfaces_as_an_exception_not_a_false_answer()
    {
        var options = ConfigurationOptions.Parse("127.0.0.1:1");
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 500;
        options.AsyncTimeout = 500;
        await using var dead = await ConnectionMultiplexer.ConnectAsync(options);
        var store = new RedisIdempotencyStore(dead);

        var act = async () => await store.TryBeginAsync("x", "fp", TimeSpan.FromSeconds(1), CancellationToken.None);

        await act.Should().ThrowAsync<RedisException>();
    }
}
