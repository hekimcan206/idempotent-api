using IdempotentApi.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace IdempotentApi.Redis;

public static class RedisServiceExtensions
{
    /// <summary>
    /// Uses Redis for idempotency state. Registers an <see cref="IConnectionMultiplexer"/> for
    /// <paramref name="configuration"/> unless the application already registered one.
    /// </summary>
    public static IServiceCollection AddRedisIdempotencyStore(this IServiceCollection services, string configuration, Action<RedisIdempotencyStoreOptions>? configure = null)
    {
        AddMultiplexer(services, configuration);
        var options = new RedisIdempotencyStoreOptions();
        configure?.Invoke(options);
        services.RemoveAll<IIdempotencyStore>();
        services.AddSingleton<IIdempotencyStore>(sp => new RedisIdempotencyStore(sp.GetRequiredService<IConnectionMultiplexer>(), options));
        return services;
    }

    /// <summary>Uses Redis for token buckets, so a limit holds across all instances rather than per instance.</summary>
    public static IServiceCollection AddRedisTokenBuckets(this IServiceCollection services, string configuration, int database = -1)
    {
        AddMultiplexer(services, configuration);
        services.RemoveAll<ITokenBucketStore>();
        services.AddSingleton<ITokenBucketStore>(sp => new RedisTokenBucketStore(sp.GetRequiredService<IConnectionMultiplexer>(), database));
        return services;
    }

    private static void AddMultiplexer(IServiceCollection services, string configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        services.TryAddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(configuration);
            // Start even if Redis is briefly unreachable: requests then get the configured fail-open / fail-closed
            // answer instead of the whole application failing to boot.
            options.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(options);
        });
    }
}
