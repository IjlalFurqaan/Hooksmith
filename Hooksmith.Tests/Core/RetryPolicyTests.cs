using Hooksmith.Core.Retry;
using Microsoft.Extensions.Time.Testing;

namespace Hooksmith.Tests.Core;

/// <summary>
/// Verifies the equal-jitter backoff schedule. Every test here runs against
/// <see cref="FakeTimeProvider"/> and a seeded <see cref="Random"/>, so the whole class
/// executes in microseconds — nothing sleeps, and nothing depends on the wall clock.
/// </summary>
public sealed class RetryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The windows the spec calls out: attempt n lands in [2^(n-1)/2, 2^(n-1)) seconds.</summary>
    public static TheoryData<int, double, double> ExpectedWindows() => new()
    {
        { 1, 0.5, 1 },
        { 2, 1, 2 },
        { 3, 2, 4 },
        { 4, 4, 8 },
        { 5, 8, 16 },
        { 6, 16, 32 },
    };

    [Theory]
    [MemberData(nameof(ExpectedWindows))]
    public void DelayForAttempt_stays_inside_its_window_across_many_draws(
        int attemptNumber,
        double lowerInclusiveSeconds,
        double upperExclusiveSeconds)
    {
        var policy = new RetryPolicy(new FakeTimeProvider(Now), new Random(Seed: 1234));

        for (var i = 0; i < 10_000; i++)
        {
            var delay = policy.DelayForAttempt(attemptNumber);

            delay.TotalSeconds.Should().BeGreaterThanOrEqualTo(lowerInclusiveSeconds);
            delay.TotalSeconds.Should().BeLessThan(upperExclusiveSeconds);
        }
    }

    [Theory]
    [MemberData(nameof(ExpectedWindows))]
    public void DelayForAttempt_actually_spreads_across_its_window(
        int attemptNumber,
        double lowerInclusiveSeconds,
        double upperExclusiveSeconds)
    {
        var policy = new RetryPolicy(new FakeTimeProvider(Now), new Random(Seed: 4321));
        var midpoint = (lowerInclusiveSeconds + upperExclusiveSeconds) / 2;

        var delays = Enumerable.Range(0, 1_000)
            .Select(_ => policy.DelayForAttempt(attemptNumber).TotalSeconds)
            .ToList();

        // The failure this guards against is a policy that computes the floor and forgets the
        // jitter term: it would satisfy the bounds test above and still stampede a recovering
        // subscriber. Requiring draws on both sides of the midpoint catches that.
        delays.Should().Contain(d => d < midpoint);
        delays.Should().Contain(d => d > midpoint);
        delays.Distinct().Should().HaveCountGreaterThan(900, "the jitter must be continuous, not a handful of buckets");
    }

    [Fact]
    public void Backoff_is_monotonic_across_attempt_numbers()
    {
        var policy = new RetryPolicy(new FakeTimeProvider(Now), new Random(Seed: 99));

        // The floor of each window is the ceiling of the previous one, so even the unluckiest
        // draw for attempt n+1 is no sooner than the luckiest draw for attempt n.
        for (var attempt = 1; attempt < RetryPolicy.MaxAttempts; attempt++)
        {
            var maxOfThis = Enumerable.Range(0, 500).Max(_ => policy.DelayForAttempt(attempt).TotalSeconds);
            var minOfNext = Enumerable.Range(0, 500).Min(_ => policy.DelayForAttempt(attempt + 1).TotalSeconds);

            minOfNext.Should().BeGreaterThanOrEqualTo(maxOfThis);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(RetryPolicy.MaxAttempts + 1)]
    [InlineData(1000)]
    public void DelayForAttempt_rejects_attempt_numbers_outside_the_budget(int attemptNumber)
    {
        var policy = new RetryPolicy(new FakeTimeProvider(Now));

        var act = () => policy.DelayForAttempt(attemptNumber);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    public void TryGetNextAttemptTime_schedules_relative_to_the_injected_clock(
        int attemptCount,
        double windowUpperExclusiveSeconds)
    {
        var time = new FakeTimeProvider(Now);
        var policy = new RetryPolicy(time, new Random(Seed: 7));

        policy.TryGetNextAttemptTime(attemptCount, out var nextAttemptAt).Should().BeTrue();

        nextAttemptAt.Should().BeAfter(Now);
        nextAttemptAt.Should().BeBefore(Now.AddSeconds(windowUpperExclusiveSeconds));
    }

    [Fact]
    public void TryGetNextAttemptTime_moves_with_the_clock()
    {
        var time = new FakeTimeProvider(Now);
        var policy = new RetryPolicy(time, new Random(Seed: 7));

        time.Advance(TimeSpan.FromDays(3));
        policy.TryGetNextAttemptTime(attemptCount: 1, out var nextAttemptAt).Should().BeTrue();

        nextAttemptAt.Should().BeAfter(Now.AddDays(3));
    }

    [Fact]
    public void TryGetNextAttemptTime_reports_exhaustion_at_the_attempt_ceiling()
    {
        var policy = new RetryPolicy(new FakeTimeProvider(Now));

        policy.TryGetNextAttemptTime(RetryPolicy.MaxAttempts - 1, out _).Should().BeTrue(
            "a delivery with 5 attempts spent still gets its 6th");
        policy.TryGetNextAttemptTime(RetryPolicy.MaxAttempts, out var exhausted).Should().BeFalse(
            "the 6th attempt is the last one; there is no 7th to schedule");
        exhausted.Should().Be(default);
    }

    [Fact]
    public void HasAttemptsRemaining_matches_the_six_attempt_budget()
    {
        for (var spent = 0; spent < RetryPolicy.MaxAttempts; spent++)
        {
            RetryPolicy.HasAttemptsRemaining(spent).Should().BeTrue();
        }

        RetryPolicy.HasAttemptsRemaining(RetryPolicy.MaxAttempts).Should().BeFalse();
        RetryPolicy.HasAttemptsRemaining(RetryPolicy.MaxAttempts + 1).Should().BeFalse();
    }

    [Fact]
    public void A_full_exhausted_schedule_completes_within_about_a_minute()
    {
        var policy = new RetryPolicy(new FakeTimeProvider(Now), new Random(Seed: 11));

        // 0.5+1+2+4+8+16 = 31.5s at the fastest, 1+2+4+8+16+32 = 63s at the slowest.
        // Worth pinning: it is the answer to "how long until this dead-letters?".
        var total = Enumerable.Range(1, RetryPolicy.MaxAttempts)
            .Aggregate(TimeSpan.Zero, (acc, attempt) => acc + policy.DelayForAttempt(attempt));

        total.Should().BeGreaterThan(TimeSpan.FromSeconds(31.5));
        total.Should().BeLessThan(TimeSpan.FromSeconds(63));
    }
}
