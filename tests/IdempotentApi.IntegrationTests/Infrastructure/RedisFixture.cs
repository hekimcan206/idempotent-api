using StackExchange.Redis;
using Testcontainers.Redis;

[assembly: AssemblyFixture(typeof(IdempotentApi.IntegrationTests.Infrastructure.RedisFixture))]

namespace IdempotentApi.IntegrationTests.Infrastructure;

/// <summary>One Redis 7 container for the whole test assembly; tests isolate themselves with unique keys.</summary>
public sealed class RedisFixture : IAsyncLifetime
{
    // The same image docker compose uses, so tests exercise the Lua scripts on the Redis version that ships.
    public const string Image = "redis:7-alpine";

    private readonly RedisContainer _container = new RedisBuilder(Image).Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public IConnectionMultiplexer Redis { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
        Redis = await Connect();
    }

    /// <summary>A separate connection, standing in for another application instance.</summary>
    public async Task<IConnectionMultiplexer> Connect()
    {
        var options = ConfigurationOptions.Parse(ConnectionString);
        options.ConnectTimeout = 15_000;
        options.AsyncTimeout = 10_000;
        options.SyncTimeout = 10_000;
        return await ConnectionMultiplexer.ConnectAsync(options);
    }

    public async ValueTask DisposeAsync()
    {
        if (Redis is not null)
        {
            await Redis.DisposeAsync();
        }

        await _container.DisposeAsync();
    }
}
