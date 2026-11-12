using System.Net;
using System.Text;
using DemoDriver;
using IdempotentApi.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace IdempotentApi.IntegrationTests;

/// <summary>
/// Runs the docker compose demo's scenarios against two in-process instances of the sample API sharing the test
/// Redis, so the demo cannot silently rot between compose runs.
/// </summary>
public sealed class SampleDemoTests(RedisFixture redis) : IAsyncLifetime
{
    private PaymentsApiFactory _a = null!;
    private PaymentsApiFactory _b = null!;

    public ValueTask InitializeAsync()
    {
        _a = new PaymentsApiFactory(redis.ConnectionString, "api-a");
        _b = new PaymentsApiFactory(redis.ConnectionString, "api-b");
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Every_demo_scenario_passes_against_two_instances()
    {
        var output = new StringWriter();
        var results = await new DemoScenarios([_a.CreateClient(), _b.CreateClient()], output).RunAllAsync(TestContext.Current.CancellationToken);

        results.Should().HaveCount(6);
        results.Where(r => !r.Passed).Should().BeEmpty(output.ToString());
    }

    [Fact]
    public async Task Readiness_reports_redis_and_validation_errors_are_replayed_like_any_4xx()
    {
        using var ready = await _a.CreateClient().GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        ready.StatusCode.Should().Be(HttpStatusCode.OK);

        HttpRequestMessage Invalid()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/payments")
            {
                Content = new StringContent("""{"amount":"-5","currency":"try","merchantReference":""}""", Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-Client-Id", "validation-" + Guid.NewGuid().ToString("N"));
            request.Headers.TryAddWithoutValidation("Idempotency-Key", "\"validation-key-0001\"");
            return request;
        }

        using var invalidRequest = Invalid();
        using var first = await _a.CreateClient().SendAsync(invalidRequest, TestContext.Current.CancellationToken);
        var retryRequest = Invalid();
        retryRequest.Headers.Remove("X-Client-Id");
        retryRequest.Headers.Add("X-Client-Id", invalidRequest.Headers.GetValues("X-Client-Id").Single());
        using var retry = await _b.CreateClient().SendAsync(retryRequest, TestContext.Current.CancellationToken);

        first.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("amount").And.Contain("currency").And.Contain("merchantReference");
        retry.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        retry.Headers.Contains(IdempotencyMiddleware.ReplayedHeader).Should().BeTrue();
        retryRequest.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _a.DisposeAsync();
        await _b.DisposeAsync();
    }

    private sealed class PaymentsApiFactory(string redisConnection, string instance) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Redis", redisConnection);
            builder.UseSetting("Payments:InstanceName", instance);
            builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        }
    }
}
