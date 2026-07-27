using System.Text;
using Hooksmith.Core;

namespace Hooksmith.Tests.Core;

/// <summary>
/// Covers the small amount of behaviour that lives on the domain records themselves:
/// subscription matching and response-body truncation.
/// </summary>
public sealed class DomainRecordTests
{
    [Theory]
    [InlineData("invoice.paid", true)]
    [InlineData("INVOICE.PAID", true)]
    [InlineData("Invoice.Paid", true)]
    [InlineData("customer.created", true)]
    [InlineData("invoice.voided", false)]
    [InlineData("invoice", false)]
    [InlineData("invoice.paid.late", false)]
    [InlineData("", false)]
    public void Matches_compares_event_types_case_insensitively_and_exactly(string eventType, bool expected)
    {
        var subscription = ActiveSubscription("invoice.paid", "customer.created");

        subscription.Matches(eventType).Should().Be(expected);
    }

    [Fact]
    public void An_inactive_subscription_matches_nothing()
    {
        var subscription = ActiveSubscription("invoice.paid") with { IsActive = false };

        subscription.Matches("invoice.paid").Should().BeFalse();
    }

    [Fact]
    public void A_subscription_with_no_event_types_matches_nothing()
    {
        ActiveSubscription().Matches("invoice.paid").Should().BeFalse();
    }

    [Fact]
    public void A_short_response_body_is_stored_verbatim()
    {
        const string body = """{"error":"validation failed"}""";

        Attempt.TruncateResponseBody(body).Should().Be(body);
        Attempt.TruncateResponseBody(null).Should().BeNull();
        Attempt.TruncateResponseBody(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void An_oversized_response_body_is_capped_at_two_kilobytes()
    {
        var huge = new string('x', 10_000);

        var truncated = Attempt.TruncateResponseBody(huge);

        truncated.Should().NotBeNull();
        Encoding.UTF8.GetByteCount(truncated!).Should().BeLessThanOrEqualTo(Attempt.MaxResponseBodyBytes);
        truncated.Should().HaveLength(Attempt.MaxResponseBodyBytes);
    }

    [Fact]
    public void Truncation_counts_utf8_bytes_not_characters()
    {
        // '事' is three UTF-8 bytes, so 1000 of them is 3000 bytes and must be cut even
        // though the string is only 1000 characters long.
        var body = new string('事', 1_000);

        var truncated = Attempt.TruncateResponseBody(body);

        Encoding.UTF8.GetByteCount(truncated!).Should().BeLessThanOrEqualTo(Attempt.MaxResponseBodyBytes);
        truncated.Should().HaveLength(Attempt.MaxResponseBodyBytes / 3);
    }

    [Fact]
    public void Truncation_never_splits_a_surrogate_pair()
    {
        // The emoji is four UTF-8 bytes and two UTF-16 chars. Padding so that the cut lands
        // mid-emoji would produce a lone surrogate and unrepresentable text.
        var body = new string('a', Attempt.MaxResponseBodyBytes - 2) + "🪝🪝🪝";

        var truncated = Attempt.TruncateResponseBody(body)!;

        Encoding.UTF8.GetByteCount(truncated).Should().BeLessThanOrEqualTo(Attempt.MaxResponseBodyBytes);
        char.IsLowSurrogate(truncated[^1]).Should().BeFalse();
        char.IsHighSurrogate(truncated[^1]).Should().BeFalse();
        Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(truncated)).Should().Be(truncated);
    }

    [Fact]
    public void Attempt_Create_truncates_on_the_way_in()
    {
        var attempt = Attempt.Create(
            deliveryId: Guid.NewGuid(),
            attemptNumber: 3,
            statusCode: 500,
            responseBody: new string('y', 10_000),
            error: null,
            durationMs: 142,
            attemptedAt: DateTimeOffset.UnixEpoch);

        attempt.ResponseBody.Should().HaveLength(Attempt.MaxResponseBodyBytes);
        attempt.AttemptNumber.Should().Be(3);
        attempt.StatusCode.Should().Be(500);
        attempt.DurationMs.Should().Be(142);
    }

    private static Subscription ActiveSubscription(params string[] eventTypes) =>
        new(
            Id: Guid.NewGuid(),
            Name: "Billing service",
            Url: new Uri("https://example.test/hooks"),
            Secret: "whsec_test",
            EventTypes: eventTypes,
            IsActive: true,
            CreatedAt: DateTimeOffset.UnixEpoch);
}
