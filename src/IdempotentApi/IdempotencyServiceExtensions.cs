using IdempotentApi.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IdempotentApi;

public static class IdempotencyServiceExtensions
{
    /// <summary>
    /// Registers the middleware's services. Uses <see cref="InMemoryIdempotencyStore"/> unless another
    /// <see cref="IIdempotencyStore"/> is registered (for example by <c>AddRedisIdempotencyStore</c>).
    /// </summary>
    public static IServiceCollection AddIdempotency(this IServiceCollection services, Action<IdempotencyOptions>? configure = null)
    {
        services.AddOptions<IdempotencyOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IRequestFingerprinter, Sha256RequestFingerprinter>();
        services.TryAddSingleton<IIdempotencyStore>(sp => new InMemoryIdempotencyStore(sp.GetRequiredService<TimeProvider>()));
        return services;
    }

    /// <summary>Registers token-bucket rate limiting. In-memory buckets unless another <see cref="ITokenBucketStore"/> is registered.</summary>
    public static IServiceCollection AddTokenBucketRateLimiting(this IServiceCollection services, Action<TokenBucketRateLimitOptions> configure)
    {
        services.Configure(configure);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ITokenBucketStore>(sp => new InMemoryTokenBucketStore(sp.GetRequiredService<TimeProvider>()));
        return services;
    }

    /// <summary>Adds the idempotency middleware. Call after <c>UseRouting</c> (and after authentication, if partitions use the user).</summary>
    public static IApplicationBuilder UseIdempotency(this IApplicationBuilder app) => app.UseMiddleware<IdempotencyMiddleware>();

    /// <summary>Adds the rate-limit middleware. Place it before <see cref="UseIdempotency"/> so retries also spend quota.</summary>
    public static IApplicationBuilder UseTokenBucketRateLimiting(this IApplicationBuilder app) => app.UseMiddleware<TokenBucketRateLimitMiddleware>();

    /// <summary>Makes an endpoint (or group) idempotent with the default policy, optionally adjusted.</summary>
    public static TBuilder RequireIdempotency<TBuilder>(this TBuilder builder, Action<IdempotencyPolicy>? configure = null)
        where TBuilder : IEndpointConventionBuilder
    {
        IdempotencyPolicy? policy = null;
        if (configure is not null)
        {
            policy = new IdempotencyPolicy();
            configure(policy);
            policy.Validate();
        }

        builder.WithMetadata(new IdempotencyMetadata(policy));
        return builder;
    }

    /// <summary>Applies a registered token-bucket policy to an endpoint (or group).</summary>
    public static TBuilder RequireTokenBucket<TBuilder>(this TBuilder builder, string policyName, int cost = 1)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new TokenBucketMetadata(policyName, cost));
        return builder;
    }
}
