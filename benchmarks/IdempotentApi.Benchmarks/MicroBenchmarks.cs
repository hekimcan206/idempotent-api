using BenchmarkDotNet.Attributes;
using IdempotentApi.RateLimiting;
using Microsoft.AspNetCore.Http;

namespace IdempotentApi.Benchmarks;

/// <summary>CPU cost of the per-request work the middleware does before it talks to the store.</summary>
[MemoryDiagnoser]
public class MicroBenchmarks
{
    private const string Field = "\"8e03978e-40d5-43e8-bc93-6894a57f9324\"";
    private readonly Sha256RequestFingerprinter _fingerprinter = new();
    private readonly TokenBucketPolicy _policy = new("p", 100, 20);
    private HttpRequest _request = null!;
    private byte[] _body1K = null!;
    private byte[] _body64K = null!;

    [GlobalSetup]
    public void Setup()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/payments";
        context.Request.ContentType = "application/json; charset=utf-8";
        _request = context.Request;
        _body1K = new byte[1024];
        _body64K = new byte[64 * 1024];
        Random.Shared.NextBytes(_body1K);
        Random.Shared.NextBytes(_body64K);
    }

    [Benchmark]
    public bool ParseKey() => IdempotencyKey.TryParse(Field, false, 16, 255, out _, out _);

    [Benchmark]
    public string StorageKey() => IdempotencyMiddleware.StorageKeyFor("idem", "client:42", "8e03978e-40d5-43e8-bc93-6894a57f9324");

    [Benchmark]
    public string Fingerprint1KiB() => _fingerprinter.Compute(_request, _body1K);

    [Benchmark]
    public string Fingerprint64KiB() => _fingerprinter.Compute(_request, _body64K);

    [Benchmark]
    public TokenBucketDecision TokenBucketTake() => TokenBucketMath.Take(new TokenBucketState(50, 1_000_000), 1_500_000, _policy, 1).Decision;
}
