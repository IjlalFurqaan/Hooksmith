using Hooksmith.Core;
using Hooksmith.Core.Retry;
using Microsoft.Extensions.Time.Testing;

namespace Hooksmith.Tests.Core;

/// <summary>
/// Covers the classification of a subscriber's response, and the Retry-After override.
/// </summary>
public sealed class DispatchOutcomeTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(202)]
    [InlineData(204)]
    [InlineData(299)]
    public void Any_2xx_succeeds(int statusCode) =>
        DispatchOutcomePolicy.Classify(statusCode).Should().Be(DispatchOutcome.Succeeded);

    [Theory]
    [InlineData(408, "request timeout means try again")]
    [InlineData(429, "too many requests explicitly means not now")]
    [InlineData(500, "the subscriber is broken, not the request")]
    [InlineData(502, "a bad gateway is usually transient")]
    [InlineData(503, "service unavailable is the textbook retry case")]
    [InlineData(504, "gateway timeout")]
    [InlineData(599, "the whole 5xx range")]
    public void Retryable_statuses_retry(int statusCode, string because) =>
        DispatchOutcomePolicy.Classify(statusCode).Should().Be(DispatchOutcome.Retry, because);

    [Theory]
    [InlineData(400, "a malformed body will still be malformed in thirty seconds")]
    [InlineData(401, "credentials do not fix themselves on a timer")]
    [InlineData(403, "nor do permissions")]
    [InlineData(404, "the endpoint does not exist")]
    [InlineData(410, "and this one says so permanently")]
    [InlineData(422, "the payload is rejected on its merits")]
    public void Other_4xx_dead_letters_immediately(int statusCode, string because) =>
        DispatchOutcomePolicy.Classify(statusCode).Should().Be(DispatchOutcome.Permanent, because);

    [Theory]
    [InlineData(100)]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(304)]
    public void Informational_and_redirect_statuses_dead_letter(int statusCode) =>
        DispatchOutcomePolicy.Classify(statusCode).Should().Be(
            DispatchOutcome.Permanent,
            "a receiver still answering with a redirect after we exhausted the budget is misconfigured");

    [Fact]
    public void A_transport_failure_retries() =>
        DispatchOutcomePolicy.ForTransportFailure().Should().Be(
            DispatchOutcome.Retry,
            "timeouts, DNS failures and resets are all things a subscriber recovers from");

    [Fact]
    public void The_whole_status_code_space_is_classified_without_throwing()
    {
        for (var statusCode = 100; statusCode <= 599; statusCode++)
        {
            var classify = () => DispatchOutcomePolicy.Classify(statusCode);
            classify.Should().NotThrow($"status {statusCode} must have a defined outcome");
        }
    }

    [Fact]
    public void Retry_after_is_preferred_over_the_computed_backoff()
    {
        var time = new FakeTimeProvider(Now);
        var policy = new RetryPolicy(time, new Random(Seed: 5));

        // Attempt 2's computed window is 1-2s. A subscriber asking for 90s knows something
        // about its own recovery that the exponential curve does not.
        policy.TryGetNextAttemptTime(attemptCount: 1, TimeSpan.FromSeconds(90), out var nextAttemptAt)
            .Should().BeTrue();

        nextAttemptAt.Should().Be(Now.AddSeconds(90));
    }

    [Fact]
    public void An_absent_retry_after_falls_back_to_the_computed_backoff()
    {
        var time = new FakeTimeProvider(Now);
        var policy = new RetryPolicy(time, new Random(Seed: 5));

        policy.TryGetNextAttemptTime(attemptCount: 1, retryAfter: null, out var nextAttemptAt)
            .Should().BeTrue();

        nextAttemptAt.Should().BeAfter(Now.AddSeconds(1)).And.BeBefore(Now.AddSeconds(2));
    }

    [Fact]
    public void An_outrageous_retry_after_is_clamped()
    {
        var time = new FakeTimeProvider(Now);
        var policy = new RetryPolicy(time, new Random(Seed: 5));

        policy.TryGetNextAttemptTime(attemptCount: 1, TimeSpan.FromDays(7), out var nextAttemptAt)
            .Should().BeTrue();

        nextAttemptAt.Should().Be(
            Now + RetryPolicy.MaxRetryAfter,
            "a subscriber must not be able to pin a delivery out of sight for a week");
    }

    [Fact]
    public void A_negative_retry_after_becomes_immediate()
    {
        var time = new FakeTimeProvider(Now);
        var policy = new RetryPolicy(time, new Random(Seed: 5));

        policy.TryGetNextAttemptTime(attemptCount: 1, TimeSpan.FromSeconds(-30), out var nextAttemptAt)
            .Should().BeTrue();

        nextAttemptAt.Should().Be(Now);
    }

    [Fact]
    public void Retry_after_does_not_buy_a_seventh_attempt()
    {
        var time = new FakeTimeProvider(Now);
        var policy = new RetryPolicy(time, new Random(Seed: 5));

        policy.TryGetNextAttemptTime(RetryPolicy.MaxAttempts, TimeSpan.FromSeconds(5), out _)
            .Should().BeFalse("the attempt budget outranks anything the subscriber asks for");
    }
}
