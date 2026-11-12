using System.Net;
using System.Security.Claims;
using System.Text;
using IdempotentApi.UnitTests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using static IdempotentApi.UnitTests.Support.RequestExtensions;

namespace IdempotentApi.UnitTests;

public sealed class MiddlewareTests
{
    private int _executions;

    private Task<TestApp> StartAsync(
        Action<IdempotencyPolicy>? policy = null,
        Action<IdempotencyOptions>? options = null,
        Func<HttpContext, Task<IResult>>? handler = null) =>
        TestApp.StartAsync(
            app =>
            {
                app.MapPost("/payments", handler ?? (async context =>
                {
                    var n = Interlocked.Increment(ref _executions);
                    using var reader = new StreamReader(context.Request.Body);
                    var body = await reader.ReadToEndAsync();
                    context.Response.Headers["X-Execution"] = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    context.Response.Cookies.Append("session", "secret");
                    return Results.Created($"/payments/{n}", new { id = n, echo = body });
                })).RequireIdempotency(policy);
                app.MapPost("/open", () => Results.Ok(Interlocked.Increment(ref _executions)));
                app.MapGet("/payments/{id}", (int id) => Results.Ok(id)).RequireIdempotency();
            },
            services => services.AddIdempotency(o =>
            {
                o.MinKeyLength = 8;
                options?.Invoke(o);
            }));

    [Fact]
    public async Task Retry_after_completion_replays_the_stored_response_without_executing_again()
    {
        await using var app = await StartAsync();
        var key = NewKey();

        using var first = await app.Client.SendAsync(Post("/payments", key));
        using var retry = await app.Client.SendAsync(Post("/payments", key));

        _executions.Should().Be(1);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        retry.StatusCode.Should().Be(HttpStatusCode.Created);
        (await retry.Content.ReadAsStringAsync()).Should().Be(await first.Content.ReadAsStringAsync());
        retry.Headers.Location.Should().Be(first.Headers.Location);
        retry.Headers.GetValues("X-Execution").Should().Equal("1");
        retry.Headers.GetValues(IdempotencyMiddleware.ReplayedHeader).Should().Equal("true");
        first.Headers.Contains(IdempotencyMiddleware.ReplayedHeader).Should().BeFalse();
        retry.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
    }

    [Fact]
    public async Task Cookies_are_never_replayed()
    {
        await using var app = await StartAsync();
        var key = NewKey();

        using var first = await app.Client.SendAsync(Post("/payments", key));
        using var retry = await app.Client.SendAsync(Post("/payments", key));

        first.Headers.Contains("Set-Cookie").Should().BeTrue();
        retry.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [Fact]
    public async Task Same_key_with_a_different_body_is_422_and_does_not_execute()
    {
        await using var app = await StartAsync();
        var key = NewKey();

        using var first = await app.Client.SendAsync(Post("/payments", key, "{\"amount\":\"10.00\"}"));
        using var reused = await app.Client.SendAsync(Post("/payments", key, "{\"amount\":\"99.00\"}"));

        reused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        reused.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        _executions.Should().Be(1);
    }

    [Fact]
    public async Task Missing_key_is_400_when_required()
    {
        await using var app = await StartAsync();

        using var response = await app.Client.SendAsync(Post("/payments", key: null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _executions.Should().Be(0);
    }

    [Fact]
    public async Task Missing_key_passes_through_when_optional()
    {
        await using var app = await StartAsync(p => p.KeyRequired = false);

        using var a = await app.Client.SendAsync(Post("/payments", key: null));
        using var b = await app.Client.SendAsync(Post("/payments", key: null));

        a.StatusCode.Should().Be(HttpStatusCode.Created);
        b.StatusCode.Should().Be(HttpStatusCode.Created);
        _executions.Should().Be(2);
    }

    [Theory]
    [InlineData("not-quoted-but-long-enough")]
    [InlineData("\"short\"")]
    [InlineData("\"has\";param=1")]
    public async Task Invalid_key_is_400(string field)
    {
        await using var app = await StartAsync();

        using var response = await app.Client.SendAsync(Post("/payments", field));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _executions.Should().Be(0);
    }

    [Fact]
    public async Task Two_key_fields_are_400()
    {
        await using var app = await StartAsync();
        using var request = Post("/payments", NewKey());
        request.Headers.TryAddWithoutValidation("Idempotency-Key", NewKey());

        using var response = await app.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Bare_keys_work_when_enabled()
    {
        await using var app = await StartAsync(options: o => o.AllowBareKeys = true);
        var key = Guid.NewGuid().ToString();

        using var first = await app.Client.SendAsync(Post("/payments", key));
        using var retry = await app.Client.SendAsync(Post("/payments", key));

        retry.Headers.Contains(IdempotencyMiddleware.ReplayedHeader).Should().BeTrue();
        _executions.Should().Be(1);
    }

    [Fact]
    public async Task Endpoints_without_metadata_and_safe_methods_are_untouched()
    {
        await using var app = await StartAsync();

        using var open = await app.Client.SendAsync(Post("/open", key: null));
        using var get = await app.Client.GetAsync(new Uri("/payments/7", UriKind.Relative));

        open.StatusCode.Should().Be(HttpStatusCode.OK);
        get.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Server_errors_release_the_key_so_a_retry_executes_again()
    {
        var calls = 0;
        await using var app = await StartAsync(handler: _ =>
            Task.FromResult(Interlocked.Increment(ref calls) == 1 ? Results.StatusCode(503) : Results.Ok("second try")));
        var key = NewKey();

        using var failed = await app.Client.SendAsync(Post("/payments", key));
        using var retry = await app.Client.SendAsync(Post("/payments", key));
        using var replay = await app.Client.SendAsync(Post("/payments", key));

        failed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.Headers.Contains(IdempotencyMiddleware.ReplayedHeader).Should().BeTrue();
        calls.Should().Be(2);
    }

    [Fact]
    public async Task Client_errors_are_stored_and_replayed()
    {
        var calls = 0;
        await using var app = await StartAsync(handler: _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Results.Problem(statusCode: 402, title: "Card declined"));
        });
        var key = NewKey();

        using var first = await app.Client.SendAsync(Post("/payments", key));
        using var retry = await app.Client.SendAsync(Post("/payments", key));

        retry.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
        retry.Headers.Contains(IdempotencyMiddleware.ReplayedHeader).Should().BeTrue();
        calls.Should().Be(1);
    }

    [Fact]
    public async Task An_exception_releases_the_key()
    {
        var calls = 0;
        await using var app = await StartAsync(handler: _ =>
            Interlocked.Increment(ref calls) == 1 ? throw new InvalidOperationException("boom") : Task.FromResult(Results.Ok()));
        var key = NewKey();

        // TestServer surfaces unhandled exceptions to the caller instead of a 500.
        var failed = () => app.Client.SendAsync(Post("/payments", key));
        await failed.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        using var retry = await app.Client.SendAsync(Post("/payments", key));

        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task Concurrent_duplicate_is_409_in_reject_mode()
    {
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        await using var app = await StartAsync(handler: async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return Results.Ok("done");
        });
        var key = NewKey();

        var first = app.Client.SendAsync(Post("/payments", key));
        await entered.Task;
        using var duplicate = await app.Client.SendAsync(Post("/payments", key));
        release.SetResult();
        using var firstResponse = await first;

        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        duplicate.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(1));
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Concurrent_duplicate_waits_and_gets_the_replay_in_wait_mode()
    {
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var calls = 0;
        await using var app = await StartAsync(p => p.ConcurrencyMode = ConcurrencyMode.WaitForResult, handler: async _ =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task;
            return Results.Ok("done");
        });
        var key = NewKey();

        var first = app.Client.SendAsync(Post("/payments", key));
        await entered.Task;
        var duplicate = app.Client.SendAsync(Post("/payments", key));
        await Task.Delay(100);
        duplicate.IsCompleted.Should().BeFalse("the duplicate must be held while the first request runs");
        release.SetResult();

        using var firstResponse = await first;
        using var duplicateResponse = await duplicate;
        duplicateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        duplicateResponse.Headers.Contains(IdempotencyMiddleware.ReplayedHeader).Should().BeTrue();
        calls.Should().Be(1);
    }

    [Fact]
    public async Task Wait_mode_gives_up_with_409_after_the_timeout()
    {
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        await using var app = await StartAsync(
            p =>
            {
                p.ConcurrencyMode = ConcurrencyMode.WaitForResult;
                p.WaitTimeout = TimeSpan.FromMilliseconds(200);
            },
            handler: async _ =>
            {
                entered.TrySetResult();
                await release.Task;
                return Results.Ok();
            });
        var key = NewKey();

        var first = app.Client.SendAsync(Post("/payments", key));
        await entered.Task;
        using var duplicate = await app.Client.SendAsync(Post("/payments", key));
        release.SetResult();
        (await first).Dispose();

        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Wait_mode_waiter_executes_when_the_owner_fails()
    {
        var calls = 0;
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        await using var app = await StartAsync(p => p.ConcurrencyMode = ConcurrencyMode.WaitForResult, handler: async _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult();
                await release.Task;
                return Results.StatusCode(500);
            }

            return Results.Ok("recovered");
        });
        var key = NewKey();

        var first = app.Client.SendAsync(Post("/payments", key));
        await entered.Task;
        var waiter = app.Client.SendAsync(Post("/payments", key));
        await Task.Delay(50);
        release.SetResult();

        using var firstResponse = await first;
        using var waiterResponse = await waiter;
        firstResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        waiterResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        waiterResponse.Headers.Contains(IdempotencyMiddleware.ReplayedHeader).Should().BeFalse();
        calls.Should().Be(2);
    }

    [Fact]
    public async Task Oversized_request_body_is_413()
    {
        await using var app = await StartAsync(options: o => o.MaxRequestBodyBytes = 16);

        using var response = await app.Client.SendAsync(Post("/payments", NewKey(), new string('x', 17)));

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        _executions.Should().Be(0);
    }

    [Fact]
    public async Task Oversized_response_executes_once_and_retries_get_409_not_a_second_execution()
    {
        await using var app = await StartAsync(options: o => o.MaxResponseBodyBytes = 64);
        var key = NewKey();
        var bigBody = new string('y', 200);

        using var first = await app.Client.SendAsync(Post("/payments", key, bigBody));
        using var retry = await app.Client.SendAsync(Post("/payments", key, bigBody));

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        (await first.Content.ReadAsStringAsync()).Should().Contain(bigBody, "the client still receives the full response");
        retry.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await retry.Content.ReadAsStringAsync()).Should().Contain("\"original-status\":201");
        _executions.Should().Be(1);
    }

    [Fact]
    public async Task Keys_are_scoped_to_the_caller()
    {
        await using var app = await TestApp.StartAsync(
            a => a.MapPost("/payments", () => Results.Ok(Interlocked.Increment(ref _executions))).RequireIdempotency(),
            services => services.AddIdempotency(o => o.PartitionResolver = c => c.Request.Headers["X-Client"].ToString()));
        var key = NewKey();

        HttpRequestMessage As(string client)
        {
            var request = Post("/payments", key);
            request.Headers.Add("X-Client", client);
            return request;
        }

        using var alice = await app.Client.SendAsync(As("alice"));
        using var bob = await app.Client.SendAsync(As("bob"));
        using var aliceAgain = await app.Client.SendAsync(As("alice"));

        _executions.Should().Be(2);
        (await bob.Content.ReadAsStringAsync()).Should().Be("2");
        aliceAgain.Headers.Contains(IdempotencyMiddleware.ReplayedHeader).Should().BeTrue();
    }

    [Fact]
    public async Task Default_partition_uses_the_authenticated_user()
    {
        var context = new DefaultHttpContext();
        IdempotencyOptions.DefaultPartition(context).Should().Be("anonymous");

        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user-42")], "test"));
        IdempotencyOptions.DefaultPartition(context).Should().Be("user-42");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Store_failure_is_503_and_the_handler_does_not_run()
    {
        await using var app = await TestApp.StartAsync(
            a => a.MapPost("/payments", () => Results.Ok(Interlocked.Increment(ref _executions))).RequireIdempotency(),
            services => services.AddSingleton<IIdempotencyStore>(new ThrowingStore()));

        using var response = await app.Client.SendAsync(Post("/payments", NewKey()));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(1));
        _executions.Should().Be(0);
    }

    [Fact]
    public async Task Handler_sees_the_feature_and_its_fencing_token()
    {
        await using var app = await TestApp.StartAsync(a => a.MapPost("/payments", (HttpContext c) =>
        {
            var feature = c.Features.Get<IIdempotencyFeature>()!;
            return Results.Ok(new { feature.Key, feature.FencingToken, lost = feature.LeaseLost.IsCancellationRequested });
        }).RequireIdempotency());

        using var response = await app.Client.SendAsync(Post("/payments", "\"feature-key-123456\""));

        (await response.Content.ReadAsStringAsync()).Should().Be("{\"key\":\"feature-key-123456\",\"fencingToken\":1,\"lost\":false}");
    }

    [Fact]
    public async Task Body_written_through_the_pipe_writer_is_captured_completely()
    {
        await using var app = await StartAsync(handler: async context =>
        {
            context.Response.StatusCode = 200;
            await context.Response.BodyWriter.WriteAsync(Encoding.UTF8.GetBytes("pipe-"));
            await context.Response.WriteAsync("and-stream");
            return Results.Empty;
        });
        var key = NewKey();

        using var first = await app.Client.SendAsync(Post("/payments", key));
        using var retry = await app.Client.SendAsync(Post("/payments", key));

        (await first.Content.ReadAsStringAsync()).Should().Be("pipe-and-stream");
        (await retry.Content.ReadAsStringAsync()).Should().Be("pipe-and-stream");
    }

    [Fact]
    public void Storage_key_is_a_hash_of_partition_and_key()
    {
        var a = IdempotencyMiddleware.StorageKeyFor("idem", "alice", "k");
        a.Should().MatchRegex("^idem:[0-9a-f]{64}$");
        IdempotencyMiddleware.StorageKeyFor("idem", "bob", "k").Should().NotBe(a);
        IdempotencyMiddleware.StorageKeyFor("idem", "alice\nk", string.Empty).Should().NotBe(a, "the partition is length-prefixed");
    }

    [Fact]
    public void Policy_validation_rejects_nonsense()
    {
        var act = () => new IdempotencyPolicy { Lease = TimeSpan.FromMilliseconds(10) }.Validate();
        act.Should().Throw<InvalidOperationException>();
        var noRetention = () => new IdempotencyPolicy { Retention = TimeSpan.Zero }.Validate();
        noRetention.Should().Throw<InvalidOperationException>();
        var badLengths = () => new IdempotencyOptions { MinKeyLength = 10, MaxKeyLength = 5 }.Validate();
        badLengths.Should().Throw<InvalidOperationException>();
    }

    private sealed class ThrowingStore : IIdempotencyStore
    {
        public ValueTask<BeginResult> TryBeginAsync(string storageKey, string fingerprint, TimeSpan lease, CancellationToken cancellationToken) =>
            throw new TimeoutException("store down");

        public ValueTask<bool> RenewAsync(string storageKey, string ownerToken, TimeSpan lease, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<bool> CompleteAsync(string storageKey, string ownerToken, StoredResponse response, TimeSpan retention, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<bool> ReleaseAsync(string storageKey, string ownerToken, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
