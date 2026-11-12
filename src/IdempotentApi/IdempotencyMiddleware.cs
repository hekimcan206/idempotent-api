using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace IdempotentApi;

/// <summary>
/// Implements the server side of draft-ietf-httpapi-idempotency-key-header for endpoints marked with
/// <c>RequireIdempotency()</c>. Must run after routing (it reads endpoint metadata) and before the endpoint.
/// </summary>
public sealed partial class IdempotencyMiddleware(
    RequestDelegate next,
    IIdempotencyStore store,
    IRequestFingerprinter fingerprinter,
    IOptions<IdempotencyOptions> options,
    TimeProvider timeProvider,
    ILogger<IdempotencyMiddleware> logger)
{
    /// <summary>Set on every replayed response so clients and logs can tell a replay from a fresh execution.</summary>
    public const string ReplayedHeader = "Idempotent-Replayed";

    // The draft's own example value; a fixed example keeps error bodies deterministic.
    private const string ExampleKey = "8e03978e-40d5-43e8-bc93-6894a57f9324";

    // Headers that describe this particular transmission rather than the outcome of the operation.
    private static readonly HashSet<string> NonReplayableHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Date", "Server", "Content-Length", "Transfer-Encoding", "Connection", "Keep-Alive", "Set-Cookie",
        "RateLimit", "RateLimit-Policy", "Retry-After", ReplayedHeader, "traceparent", "tracestate", "Request-Id",
    };

    private readonly IdempotencyOptions _options = Validated(options.Value);

    public async Task InvokeAsync(HttpContext context)
    {
        var metadata = context.GetEndpoint()?.Metadata.GetMetadata<IdempotencyMetadata>();
        if (metadata is null || IsSafeMethod(context.Request.Method))
        {
            await next(context);
            return;
        }

        var policy = metadata.Policy ?? _options.DefaultPolicy;
        var problemBase = _options.ProblemTypeBase;

        var values = context.Request.Headers[_options.HeaderName];
        if (values.Count == 0)
        {
            if (!policy.KeyRequired)
            {
                IdempotencyTelemetry.Record(IdempotencyOutcomes.Unprotected);
                await next(context);
                return;
            }

            IdempotencyTelemetry.Record(IdempotencyOutcomes.MissingKey);
            await Problems.WriteAsync(context, problemBase, "missing-key", StatusCodes.Status400BadRequest,
                "Idempotency-Key is missing",
                $"This operation is idempotent and requires the {_options.HeaderName} header, for example {_options.HeaderName}: \"{ExampleKey}\".");
            return;
        }

        if (!TryReadKey(values, out var key, out var parseError))
        {
            IdempotencyTelemetry.Record(IdempotencyOutcomes.InvalidKey);
            await Problems.WriteAsync(context, problemBase, "invalid-key", StatusCodes.Status400BadRequest,
                "Idempotency-Key is invalid",
                DescribeParseError(parseError));
            return;
        }

        var body = await TryBufferBodyAsync(context);
        if (body is null)
        {
            IdempotencyTelemetry.Record(IdempotencyOutcomes.BodyTooLarge);
            await Problems.WriteAsync(context, problemBase, "body-too-large", StatusCodes.Status413PayloadTooLarge,
                "Request body too large for an idempotent request",
                $"Idempotent requests are fingerprinted in full; the limit is {_options.MaxRequestBodyBytes} bytes.");
            return;
        }

        var fingerprint = fingerprinter.Compute(context.Request, body);
        var storageKey = StorageKeyFor(_options.KeyPrefix, _options.PartitionResolver(context), key);
        using var activity = IdempotencyTelemetry.ActivitySource.StartActivity("idempotency");
        activity?.SetTag("idempotency.storage_key", storageKey);

        BeginResult begin;
        try
        {
            begin = await ResolveAsync(storageKey, fingerprint, policy, context.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
        {
            // Fail closed: without the store there is no way to know whether this key already executed.
            LogStoreUnavailable(logger, ex, storageKey);
            IdempotencyTelemetry.Record(IdempotencyOutcomes.StoreUnavailable);
            context.Response.Headers.RetryAfter = "1";
            await Problems.WriteAsync(context, problemBase, "store-unavailable", StatusCodes.Status503ServiceUnavailable,
                "Idempotency store unavailable",
                "The request was not executed because its idempotency state could not be checked. Retry with the same key.");
            return;
        }

        switch (begin.Outcome)
        {
            case BeginOutcome.Acquired:
                await ExecuteAsync(context, key, storageKey, begin.Lease!, policy);
                break;

            case BeginOutcome.Completed when begin.Response!.Replayable:
                IdempotencyTelemetry.Record(IdempotencyOutcomes.Replayed);
                await ReplayAsync(context, begin.Response);
                break;

            case BeginOutcome.Completed:
                IdempotencyTelemetry.Record(IdempotencyOutcomes.NotReplayable);
                await Problems.WriteAsync(context, problemBase, "not-replayable", StatusCodes.Status409Conflict,
                    "Original response cannot be replayed",
                    $"The request with this key was executed (status {begin.Response.StatusCode.ToString(CultureInfo.InvariantCulture)}), but its response exceeded the replay size limit.",
                    w => w.WriteNumber("original-status", begin.Response.StatusCode));
                break;

            case BeginOutcome.FingerprintMismatch:
                IdempotencyTelemetry.Record(IdempotencyOutcomes.Mismatch);
                await Problems.WriteAsync(context, problemBase, "key-reused", StatusCodes.Status422UnprocessableEntity,
                    "Idempotency-Key is already used",
                    "This key was already used with a different request (method, path, query, media type or body). Use a new key for a new request.");
                break;

            default:
                IdempotencyTelemetry.Record(IdempotencyOutcomes.Conflict);
                context.Response.Headers.RetryAfter = "1";
                await Problems.WriteAsync(context, problemBase, "request-in-flight", StatusCodes.Status409Conflict,
                    "A request is outstanding for this Idempotency-Key",
                    "Another request with this key is being processed. Retry after it completes to receive its response.");
                break;
        }
    }

    internal static string StorageKeyFor(string prefix, string partition, string key)
    {
        // Hashing keeps client-chosen bytes out of the store's key space (no injection, bounded length), and the
        // partition makes the same client key from two callers two different records. The partition is
        // length-prefixed so no (partition, key) pair can be re-split into another one.
        var material = Encoding.UTF8.GetBytes(partition.Length.ToString(CultureInfo.InvariantCulture) + ":" + partition + "\n" + key);
        return prefix + ":" + Convert.ToHexStringLower(SHA256.HashData(material));
    }

    private async Task<BeginResult> ResolveAsync(string storageKey, string fingerprint, IdempotencyPolicy policy, CancellationToken cancellationToken)
    {
        var result = await store.TryBeginAsync(storageKey, fingerprint, policy.Lease, cancellationToken);
        if (result.Outcome != BeginOutcome.InFlight || policy.ConcurrencyMode != ConcurrencyMode.WaitForResult)
        {
            return result;
        }

        // Poll with capped exponential backoff and jitter. A pub/sub wake-up would answer a few milliseconds sooner but
        // needs a subscription per waiting request; polling also covers the case where the owner crashes, because
        // the lapsed lease lets one waiter claim the key and execute.
        var started = timeProvider.GetTimestamp();
        var delay = TimeSpan.FromMilliseconds(5);
        while (timeProvider.GetElapsedTime(started) < policy.WaitTimeout)
        {
            var jitter = TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * delay.TotalMilliseconds * 0.5);
            await Task.Delay(delay + jitter, timeProvider, cancellationToken);
            result = await store.TryBeginAsync(storageKey, fingerprint, policy.Lease, cancellationToken);
            if (result.Outcome != BeginOutcome.InFlight)
            {
                IdempotencyTelemetry.RecordWait(timeProvider.GetElapsedTime(started));
                return result;
            }

            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 100));
        }

        IdempotencyTelemetry.RecordWait(timeProvider.GetElapsedTime(started));
        return result;
    }

    private async Task ExecuteAsync(HttpContext context, string key, string storageKey, IdempotencyLease lease, IdempotencyPolicy policy)
    {
        using var leaseLost = new CancellationTokenSource();
        using var stopRenewal = new CancellationTokenSource();
        var renewal = RenewUntilStoppedAsync(storageKey, lease.OwnerToken, policy.Lease, leaseLost, stopRenewal.Token);

        context.Features.Set<IIdempotencyFeature>(new IdempotencyFeature(key, storageKey, lease.FencingToken, leaseLost.Token));
        var originalBody = context.Features.Get<IHttpResponseBodyFeature>()!;
        using var capture = new ResponseCapture(originalBody.Stream, _options.MaxResponseBodyBytes);
        var captureBody = new StreamResponseBodyFeature(capture, originalBody);
        context.Features.Set<IHttpResponseBodyFeature>(captureBody);

        try
        {
            await next(context);
            // Flush whatever the handler left buffered in the PipeWriter so the capture holds the whole body.
            await captureBody.CompleteAsync();
        }
        catch
        {
            await StopAsync(stopRenewal, renewal);
            await TryReleaseAsync(storageKey, lease.OwnerToken);
            IdempotencyTelemetry.Record(IdempotencyOutcomes.Released);
            throw;
        }
        finally
        {
            context.Features.Set(originalBody);
        }

        await StopAsync(stopRenewal, renewal);
        var status = context.Response.StatusCode;

        // The outcome is recorded even if the client has gone away: the side effect happened, and the retry that
        // follows a dropped connection is exactly the request that must get this response instead of a re-execution.
        if (!_options.IsCacheableStatus(status))
        {
            await TryReleaseAsync(storageKey, lease.OwnerToken);
            IdempotencyTelemetry.Record(IdempotencyOutcomes.Released);
            return;
        }

        var stored = capture.Overflowed
            ? new StoredResponse(status, [], ReadOnlyMemory<byte>.Empty, Replayable: false)
            : new StoredResponse(status, ReplayableHeaders(context.Response.Headers), capture.Captured.ToArray());

        bool completed;
        try
        {
            completed = await store.CompleteAsync(storageKey, lease.OwnerToken, stored, policy.Retention, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The client already has its response. The lease will lapse and a retry will execute again: the one
            // window where at-most-once is lost, and why the store should be highly available.
            LogCompleteFailed(logger, ex, storageKey);
            IdempotencyTelemetry.Record(IdempotencyOutcomes.StoreUnavailable);
            return;
        }

        if (!completed)
        {
            LogLeaseLost(logger, storageKey, lease.FencingToken);
            IdempotencyTelemetry.Record(IdempotencyOutcomes.LeaseLost);
            return;
        }

        IdempotencyTelemetry.Record(IdempotencyOutcomes.Executed);
    }

    private async Task RenewUntilStoppedAsync(string storageKey, string ownerToken, TimeSpan lease, CancellationTokenSource leaseLost, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(lease / 3, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                bool renewed;
                try
                {
                    renewed = await store.RenewAsync(storageKey, ownerToken, lease, stop);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A transient store error is not yet a lost lease; the next tick tries again before it lapses.
                    LogRenewFailed(logger, ex, storageKey);
                    continue;
                }

                if (!renewed)
                {
                    await leaseLost.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    private static async Task StopAsync(CancellationTokenSource stopRenewal, Task renewal)
    {
        await stopRenewal.CancelAsync();
        await renewal;
    }

    private async Task TryReleaseAsync(string storageKey, string ownerToken)
    {
        try
        {
            await store.ReleaseAsync(storageKey, ownerToken, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Not fatal: the lease lapses on its own and the key becomes claimable again.
            LogReleaseFailed(logger, ex, storageKey);
        }
    }

    private static async Task ReplayAsync(HttpContext context, StoredResponse stored)
    {
        var response = context.Response;
        response.StatusCode = stored.StatusCode;
        foreach (var (name, value) in stored.Headers)
        {
            response.Headers.Append(name, value);
        }

        response.Headers[ReplayedHeader] = "true";
        response.ContentLength = stored.Body.Length;
        if (stored.Body.Length > 0)
        {
            await response.Body.WriteAsync(stored.Body, context.RequestAborted);
        }
    }

    private static List<KeyValuePair<string, string>> ReplayableHeaders(IHeaderDictionary headers)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var (name, values) in headers)
        {
            if (NonReplayableHeaders.Contains(name))
            {
                continue;
            }

            foreach (var value in values)
            {
                if (value is not null)
                {
                    list.Add(new(name, value));
                }
            }
        }

        return list;
    }

    private bool TryReadKey(StringValues values, out string key, out IdempotencyKey.ParseError error)
    {
        key = string.Empty;
        if (values.Count != 1)
        {
            // Two Idempotency-Key fields would be combined into a List, which is not a valid Item.
            error = IdempotencyKey.ParseError.TrailingCharacters;
            return false;
        }

        if (!IdempotencyKey.TryParse(values[0], _options.AllowBareKeys, _options.MinKeyLength, _options.MaxKeyLength, out var parsed, out error))
        {
            return false;
        }

        key = parsed;
        return true;
    }

    private string DescribeParseError(IdempotencyKey.ParseError error) => error switch
    {
        IdempotencyKey.ParseError.NotAString => $"{_options.HeaderName} must be a quoted Structured Field string, for example \"{ExampleKey}\".",
        IdempotencyKey.ParseError.TooShort => $"{_options.HeaderName} must be at least {_options.MinKeyLength} characters; use a random value such as a UUID.",
        IdempotencyKey.ParseError.TooLong => $"{_options.HeaderName} must be at most {_options.MaxKeyLength} characters.",
        IdempotencyKey.ParseError.TrailingCharacters => $"Send exactly one {_options.HeaderName} value, without parameters.",
        _ => $"{_options.HeaderName} is not a valid Structured Field string ({error}).",
    };

    private async Task<byte[]?> TryBufferBodyAsync(HttpContext context)
    {
        var request = context.Request;
        var limit = _options.MaxRequestBodyBytes;
        if (request.ContentLength > limit)
        {
            return null;
        }

        using var buffer = new MemoryStream(request.ContentLength is { } length ? (int)length : 0);
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.ToArray();
        // The handler reads the body again from this copy.
        request.Body = new MemoryStream(bytes, writable: false);
        return bytes;
    }

    private static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);

    private static IdempotencyOptions Validated(IdempotencyOptions options)
    {
        options.Validate();
        return options;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Idempotency store unavailable for {StorageKey}; request rejected with 503")]
    private static partial void LogStoreUnavailable(ILogger logger, Exception exception, string storageKey);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not store the response for {StorageKey}; a retry may execute again once the lease lapses")]
    private static partial void LogCompleteFailed(ILogger logger, Exception exception, string storageKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lease for {StorageKey} (fence {FencingToken}) was lost before completion; the response was not stored")]
    private static partial void LogLeaseLost(ILogger logger, string storageKey, long fencingToken);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lease renewal for {StorageKey} failed; retrying on the next tick")]
    private static partial void LogRenewFailed(ILogger logger, Exception exception, string storageKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Releasing {StorageKey} failed; the lease will lapse on its own")]
    private static partial void LogReleaseFailed(ILogger logger, Exception exception, string storageKey);
}

/// <summary>Endpoint metadata that opts an endpoint in to idempotency handling.</summary>
public sealed record IdempotencyMetadata(IdempotencyPolicy? Policy);
