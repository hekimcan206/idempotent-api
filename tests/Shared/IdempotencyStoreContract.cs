using System.Text;

namespace IdempotentApi.ContractTests;

/// <summary>
/// Behaviour every <see cref="IIdempotencyStore"/> must have. Runs against the in-memory store (unit tests) and the
/// Redis store (integration tests), so the two cannot drift apart.
/// </summary>
public abstract class IdempotencyStoreContract
{
    protected static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);
    protected static readonly TimeSpan Retention = TimeSpan.FromMinutes(5);

    protected abstract IIdempotencyStore Store { get; }

    protected static string NewKey() => "contract:" + Guid.NewGuid().ToString("N");

    protected static StoredResponse Response(int status = 201, string body = "{\"id\":1}") =>
        new(status, [new("Content-Type", "application/json"), new("Location", "/payments/1"), new("X-Multi", "a"), new("X-Multi", "b")], Encoding.UTF8.GetBytes(body));

    [Fact]
    public async Task First_claim_is_acquired_with_a_lease()
    {
        var result = await Store.TryBeginAsync(NewKey(), "fp", Lease, CancellationToken.None);

        result.Outcome.Should().Be(BeginOutcome.Acquired);
        result.Lease!.OwnerToken.Should().NotBeNullOrEmpty();
        result.Lease.FencingToken.Should().BePositive();
    }

    [Fact]
    public async Task Second_claim_while_running_is_in_flight()
    {
        var key = NewKey();
        await Store.TryBeginAsync(key, "fp", Lease, CancellationToken.None);

        var second = await Store.TryBeginAsync(key, "fp", Lease, CancellationToken.None);

        second.Outcome.Should().Be(BeginOutcome.InFlight);
        second.Lease.Should().BeNull();
    }

    [Fact]
    public async Task Different_fingerprint_is_a_mismatch_while_running_and_after_completion()
    {
        var key = NewKey();
        var lease = (await Store.TryBeginAsync(key, "fp-a", Lease, CancellationToken.None)).Lease!;

        (await Store.TryBeginAsync(key, "fp-b", Lease, CancellationToken.None)).Outcome.Should().Be(BeginOutcome.FingerprintMismatch);
        await Store.CompleteAsync(key, lease.OwnerToken, Response(), Retention, CancellationToken.None);
        (await Store.TryBeginAsync(key, "fp-b", Lease, CancellationToken.None)).Outcome.Should().Be(BeginOutcome.FingerprintMismatch);
    }

    [Fact]
    public async Task Completed_response_is_returned_byte_for_byte_with_repeated_headers()
    {
        var key = NewKey();
        var lease = (await Store.TryBeginAsync(key, "fp", Lease, CancellationToken.None)).Lease!;
        var original = Response(201, "{\"amount\":\"12.50\",\"note\":\"çğış ✓\"}");

        (await Store.CompleteAsync(key, lease.OwnerToken, original, Retention, CancellationToken.None)).Should().BeTrue();
        var replay = await Store.TryBeginAsync(key, "fp", Lease, CancellationToken.None);

        replay.Outcome.Should().Be(BeginOutcome.Completed);
        replay.Response!.StatusCode.Should().Be(201);
        replay.Response.Body.ToArray().Should().Equal(original.Body.ToArray());
        replay.Response.Headers.Should().Equal(original.Headers);
        replay.Response.Replayable.Should().BeTrue();
    }

    [Fact]
    public async Task Empty_body_and_unreplayable_marker_round_trip()
    {
        var key = NewKey();
        var lease = (await Store.TryBeginAsync(key, "fp", Lease, CancellationToken.None)).Lease!;

        await Store.CompleteAsync(key, lease.OwnerToken, new StoredResponse(200, [], ReadOnlyMemory<byte>.Empty, Replayable: false), Retention, CancellationToken.None);
        var replay = await Store.TryBeginAsync(key, "fp", Lease, CancellationToken.None);

        replay.Response!.Replayable.Should().BeFalse();
        replay.Response.Body.Length.Should().Be(0);
        replay.Response.Headers.Should().BeEmpty();
    }

    [Fact]
    public async Task Only_the_owner_can_complete_renew_or_release()
    {
        var key = NewKey();
        var lease = (await Store.TryBeginAsync(key, "fp", Lease, CancellationToken.None)).Lease!;

        (await Store.CompleteAsync(key, "not-the-owner", Response(), Retention, CancellationToken.None)).Should().BeFalse();
        (await Store.RenewAsync(key, "not-the-owner", Lease, CancellationToken.None)).Should().BeFalse();
        (await Store.ReleaseAsync(key, "not-the-owner", CancellationToken.None)).Should().BeFalse();
        (await Store.TryBeginAsync(key, "fp", Lease, CancellationToken.None)).Outcome.Should().Be(BeginOutcome.InFlight);

        (await Store.RenewAsync(key, lease.OwnerToken, Lease, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Released_key_can_be_claimed_again_with_a_larger_fencing_token()
    {
        var key = NewKey();
        var first = (await Store.TryBeginAsync(key, "fp", Lease, CancellationToken.None)).Lease!;

        (await Store.ReleaseAsync(key, first.OwnerToken, CancellationToken.None)).Should().BeTrue();
        var second = await Store.TryBeginAsync(key, "fp", Lease, CancellationToken.None);

        second.Outcome.Should().Be(BeginOutcome.Acquired);
        second.Lease!.FencingToken.Should().BeGreaterThan(first.FencingToken);
        second.Lease.OwnerToken.Should().NotBe(first.OwnerToken);
    }

    [Fact]
    public async Task A_completed_record_cannot_be_released_or_overwritten()
    {
        var key = NewKey();
        var lease = (await Store.TryBeginAsync(key, "fp", Lease, CancellationToken.None)).Lease!;
        await Store.CompleteAsync(key, lease.OwnerToken, Response(201, "first"), Retention, CancellationToken.None);

        (await Store.ReleaseAsync(key, lease.OwnerToken, CancellationToken.None)).Should().BeFalse();
        (await Store.CompleteAsync(key, lease.OwnerToken, Response(500, "second"), Retention, CancellationToken.None)).Should().BeFalse();

        var replay = await Store.TryBeginAsync(key, "fp", Lease, CancellationToken.None);
        Encoding.UTF8.GetString(replay.Response!.Body.Span).Should().Be("first");
    }

    [Fact]
    public async Task Expired_lease_lets_a_new_owner_in_and_locks_the_old_owner_out()
    {
        var key = NewKey();
        var stale = (await Store.TryBeginAsync(key, "fp", TimeSpan.FromMilliseconds(300), CancellationToken.None)).Lease!;

        await Task.Delay(TimeSpan.FromMilliseconds(700));
        var takeover = await Store.TryBeginAsync(key, "fp", Lease, CancellationToken.None);

        takeover.Outcome.Should().Be(BeginOutcome.Acquired);
        takeover.Lease!.FencingToken.Should().BeGreaterThan(stale.FencingToken);
        (await Store.CompleteAsync(key, stale.OwnerToken, Response(201, "stale"), Retention, CancellationToken.None)).Should().BeFalse();
        (await Store.RenewAsync(key, stale.OwnerToken, Lease, CancellationToken.None)).Should().BeFalse();
        (await Store.CompleteAsync(key, takeover.Lease.OwnerToken, Response(201, "fresh"), Retention, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Completed_record_expires_after_retention()
    {
        var key = NewKey();
        var lease = (await Store.TryBeginAsync(key, "fp", Lease, CancellationToken.None)).Lease!;
        await Store.CompleteAsync(key, lease.OwnerToken, Response(), TimeSpan.FromMilliseconds(300), CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(700));

        (await Store.TryBeginAsync(key, "other-fp", Lease, CancellationToken.None)).Outcome.Should().Be(BeginOutcome.Acquired);
    }

    [Fact]
    public async Task Concurrent_claims_grant_exactly_one_lease()
    {
        var key = NewKey();
        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ =>
            Task.Run(async () => await Store.TryBeginAsync(key, "fp", Lease, CancellationToken.None))));

        results.Count(r => r.Outcome == BeginOutcome.Acquired).Should().Be(1);
        results.Count(r => r.Outcome == BeginOutcome.InFlight).Should().Be(63);
    }
}
