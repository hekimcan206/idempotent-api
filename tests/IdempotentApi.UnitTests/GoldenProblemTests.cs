using System.Text;
using IdempotentApi.UnitTests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using static IdempotentApi.UnitTests.Support.RequestExtensions;

namespace IdempotentApi.UnitTests;

/// <summary>Every error the idempotency middleware can produce, pinned as status line + relevant headers + body.</summary>
public sealed class GoldenProblemTests
{
    private static async Task<string> Render(HttpResponseMessage response)
    {
        var sb = new StringBuilder();
        sb.Append((int)response.StatusCode).Append('\n');
        sb.Append("Content-Type: ").Append(response.Content.Headers.ContentType).Append('\n');
        if (response.Headers.RetryAfter is { } retryAfter)
        {
            sb.Append("Retry-After: ").Append(retryAfter).Append('\n');
        }

        sb.Append('\n').Append(await response.Content.ReadAsStringAsync()).Append('\n');
        return sb.ToString();
    }

    private static Task<TestApp> Start(Action<IServiceCollection>? services = null, Func<Task<IResult>>? handler = null) =>
        TestApp.StartAsync(
            a => a.MapPost("/payments", handler ?? (() => Task.FromResult(Results.Created("/payments/1", new { id = 1 })))).RequireIdempotency(),
            services ?? (s => s.AddIdempotency(o =>
            {
                o.MaxRequestBodyBytes = 64;
                o.MaxResponseBodyBytes = 4;
            })));

    [Fact]
    public async Task Missing_key()
    {
        await using var app = await Start();
        using var response = await app.Client.SendAsync(Post("/payments", null));
        Golden.Verify("missing-key.txt", await Render(response));
    }

    [Fact]
    public async Task Bare_key_when_strings_are_required()
    {
        await using var app = await Start();
        using var response = await app.Client.SendAsync(Post("/payments", "8e03978e-40d5-43e8-bc93-6894a57f9324"));
        Golden.Verify("invalid-key-not-a-string.txt", await Render(response));
    }

    [Fact]
    public async Task Key_too_short()
    {
        await using var app = await Start();
        using var response = await app.Client.SendAsync(Post("/payments", "\"retry-1\""));
        Golden.Verify("invalid-key-too-short.txt", await Render(response));
    }

    [Fact]
    public async Task Key_reused_with_another_body()
    {
        await using var app = await Start();
        var key = NewKey();
        (await app.Client.SendAsync(Post("/payments", key, "{\"amount\":\"1.00\"}"))).Dispose();
        using var response = await app.Client.SendAsync(Post("/payments", key, "{\"amount\":\"2.00\"}"));
        Golden.Verify("key-reused.txt", await Render(response));
    }

    [Fact]
    public async Task Request_in_flight()
    {
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        await using var app = await Start(handler: async () =>
        {
            entered.TrySetResult();
            await release.Task;
            return Results.Ok();
        });
        var key = NewKey();
        var first = app.Client.SendAsync(Post("/payments", key));
        await entered.Task;
        using var response = await app.Client.SendAsync(Post("/payments", key));
        release.SetResult();
        (await first).Dispose();
        Golden.Verify("request-in-flight.txt", await Render(response));
    }

    [Fact]
    public async Task Body_too_large()
    {
        await using var app = await Start();
        using var response = await app.Client.SendAsync(Post("/payments", NewKey(), new string('x', 65)));
        Golden.Verify("body-too-large.txt", await Render(response));
    }

    [Fact]
    public async Task Not_replayable()
    {
        await using var app = await Start();
        var key = NewKey();
        (await app.Client.SendAsync(Post("/payments", key))).Dispose();
        using var response = await app.Client.SendAsync(Post("/payments", key));
        Golden.Verify("not-replayable.txt", await Render(response));
    }
}
