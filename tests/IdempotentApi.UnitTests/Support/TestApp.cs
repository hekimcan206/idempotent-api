using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IdempotentApi.UnitTests.Support;

/// <summary>A minimal in-process API: the real middleware pipeline on a TestServer, with endpoints defined per test.</summary>
internal sealed class TestApp : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestApp(WebApplication app)
    {
        _app = app;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<TestApp> StartAsync(
        Action<WebApplication> map,
        Action<IServiceCollection>? services = null,
        Action<IApplicationBuilder>? pipeline = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        services?.Invoke(builder.Services);
        // TryAdd semantics: a store or options registered by the test win over the defaults.
        builder.Services.AddIdempotency();

        var app = builder.Build();
        app.UseRouting();
        if (pipeline is null)
        {
            app.UseIdempotency();
        }
        else
        {
            pipeline(app);
        }

        map(app);
        await app.StartAsync();
        return new TestApp(app);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }
}

internal static class RequestExtensions
{
    public static HttpRequestMessage Post(string path, string? key, string body = "{\"amount\":\"10.00\"}")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return request;
    }

    public static string NewKey() => IdempotencyKey.Format(Guid.NewGuid().ToString());
}
