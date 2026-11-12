using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using IdempotentApi.RateLimiting;

namespace IdempotentApi.UnitTests;

/// <summary>
/// The guarantees a token bucket is chosen for, stated as properties over random policies and random request timings.
/// The Redis Lua script performs the same arithmetic, so these properties describe the distributed limiter too.
/// </summary>
public sealed class TokenBucketPropertyTests
{
    private const double Epsilon = 1e-6;

    private sealed record Scenario(TokenBucketPolicy Policy, (long GapMicros, int Cost)[] Requests);

    private static Arbitrary<Scenario> Scenarios() =>
        (from capacity in Gen.Choose(1, 50)
         from refillTenths in Gen.Choose(1, 1000)
         from requests in (from gap in Gen.Choose(0, 2_000_000)
                           from cost in Gen.Choose(1, capacity)
                           select ((long)gap, cost)).ArrayOf()
         select new Scenario(new TokenBucketPolicy("p", capacity, refillTenths / 10d), requests)).ToArbitrary();

    private static List<(long At, int Cost, TokenBucketState State, TokenBucketDecision Decision)> Run(Scenario s)
    {
        var results = new List<(long, int, TokenBucketState, TokenBucketDecision)>();
        TokenBucketState? state = null;
        var now = 1_700_000_000_000_000L;
        foreach (var (gap, cost) in s.Requests)
        {
            now += gap;
            var (next, decision) = TokenBucketMath.Take(state, now, s.Policy, cost);
            state = next;
            results.Add((now, cost, next, decision));
        }

        return results;
    }

    [Property(MaxTest = 300)]
    public Property Never_admits_more_than_capacity_plus_refill_over_any_interval() =>
        Prop.ForAll(Scenarios(), s =>
        {
            var run = Run(s);
            for (var i = 0; i < run.Count; i++)
            {
                double admitted = 0;
                for (var j = i; j < run.Count; j++)
                {
                    if (run[j].Decision.Allowed)
                    {
                        admitted += run[j].Cost;
                    }

                    var bound = s.Policy.Capacity + (s.Policy.RefillPerSecond * (run[j].At - run[i].At) / 1_000_000d);
                    if (admitted > bound + Epsilon)
                    {
                        return false;
                    }
                }
            }

            return true;
        });

    [Property(MaxTest = 300)]
    public Property Tokens_stay_within_zero_and_capacity() =>
        Prop.ForAll(Scenarios(), s => Run(s).All(r =>
            r.State.Tokens >= -Epsilon && r.State.Tokens <= s.Policy.Capacity + Epsilon
            && r.Decision.Remaining >= 0 && r.Decision.Remaining <= s.Policy.Capacity));

    [Property(MaxTest = 300)]
    public Property A_denied_request_succeeds_exactly_when_retry_after_says() =>
        Prop.ForAll(Scenarios(), s => Run(s).Where(r => !r.Decision.Allowed).All(r =>
        {
            var retryMicros = (long)r.Decision.RetryAfter.TotalMicroseconds;
            var early = TokenBucketMath.Take(r.State, r.At + retryMicros - 2, s.Policy, r.Cost).Decision;
            var onTime = TokenBucketMath.Take(r.State, r.At + retryMicros, s.Policy, r.Cost).Decision;
            return onTime.Allowed && (retryMicros <= 2 || !early.Allowed);
        }));

    [Property(MaxTest = 300)]
    public Property Reset_after_is_the_time_to_a_full_bucket() =>
        Prop.ForAll(Scenarios(), s => Run(s).All(r =>
        {
            var full = TokenBucketMath.Take(r.State, r.At + (long)r.Decision.ResetAfter.TotalMicroseconds, s.Policy, 0).State;
            return full.Tokens >= s.Policy.Capacity - Epsilon;
        }));

    [Fact]
    public void Continuous_demand_is_admitted_at_the_refill_rate()
    {
        // 10 tokens, 5/s, a request every millisecond for 10 s: capacity + rate * T = 10 + 50 = 60.
        var policy = new TokenBucketPolicy("p", 10, 5);
        TokenBucketState? state = null;
        var admitted = 0;
        for (long t = 0; t <= 10_000_000; t += 1000)
        {
            var (next, decision) = TokenBucketMath.Take(state, t, policy, 1);
            state = next;
            admitted += decision.Allowed ? 1 : 0;
        }

        admitted.Should().Be(60);
    }

    [Fact]
    public void Clock_going_backwards_mints_no_tokens()
    {
        var policy = new TokenBucketPolicy("p", 2, 1);
        var (s1, _) = TokenBucketMath.Take(null, 10_000_000, policy, 1);
        var (s2, _) = TokenBucketMath.Take(s1, 10_000_000, policy, 1);
        var (_, back) = TokenBucketMath.Take(s2, 5_000_000, policy, 1);

        back.Allowed.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(5, 0.0)]
    [InlineData(5, -1.0)]
    [InlineData(5, double.NaN)]
    [InlineData(5, double.PositiveInfinity)]
    public void Policy_rejects_invalid_parameters(int capacity, double refill)
    {
        var act = () => new TokenBucketPolicy("p", capacity, refill);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Window_is_time_to_refill_from_empty() => new TokenBucketPolicy("p", 100, 30).WindowSeconds.Should().Be(4);
}
