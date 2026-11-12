using IdempotentApi;
using IdempotentApi.Redis;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PaymentsApi;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Formatting.Compact;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection("Payments").Get<PaymentsSettings>() ?? new PaymentsSettings();
var redisConnection = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";

builder.Host.UseSerilog((context, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("instance", settings.InstanceName)
    .WriteTo.Console(new RenderedCompactJsonFormatter()));

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<PaymentLedger>();

// Callers are identified by an API client id (synthetic here). The same id scopes both idempotency keys and rate
// limit buckets, so one client can neither replay nor exhaust another client's requests.
builder.Services.AddIdempotency(options => options.PartitionResolver = PaymentsEndpoints.ClientId);
builder.Services.AddRedisIdempotencyStore(redisConnection);
builder.Services.AddTokenBucketRateLimiting(options =>
{
    options.AddPolicy("payments", settings.RateLimitCapacity, settings.RateLimitRefillPerSecond);
    options.PartitionResolver = PaymentsEndpoints.ClientId;
});
builder.Services.AddRedisTokenBuckets(redisConnection);

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddCheck<RedisHealthCheck>("redis", tags: ["ready"]);

var otlp = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("payments-api", serviceInstanceId: settings.InstanceName))
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation().AddSource(IdempotencyTelemetry.Name);
        if (!string.IsNullOrEmpty(otlp))
        {
            t.AddOtlpExporter();
        }
    })
    .WithMetrics(m =>
    {
        m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddMeter(IdempotencyTelemetry.Name);
        if (!string.IsNullOrEmpty(otlp))
        {
            m.AddOtlpExporter();
        }
    });

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseRouting();
// Limits first, so retries and duplicates spend quota like any other request; then idempotency.
app.UseTokenBucketRateLimiting();
app.UseIdempotency();

app.MapOpenApi();
app.MapScalarApiReference();
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") });
app.MapPaymentsEndpoints();

app.Run();

/// <summary>Entry point, visible to integration tests.</summary>
public partial class Program;

/// <summary>Readiness: the API cannot guarantee single execution without Redis, so it is not ready without it.</summary>
internal sealed class RedisHealthCheck(IConnectionMultiplexer redis) : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    public async Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var latency = await redis.GetDatabase().PingAsync();
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy($"ping {latency.TotalMilliseconds:F1} ms");
        }
        catch (RedisException ex)
        {
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("redis unreachable", ex);
        }
    }
}
