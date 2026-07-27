using Hooksmith.Core.Signing;
using Microsoft.Extensions.Time.Testing;

namespace Hooksmith.Tests.Core;

/// <summary>
/// Covers the signature scheme end to end: round-trip, tampering, and freshness.
/// </summary>
public sealed class SignatureTests
{
    private const string Secret = "whsec_2f4a9c1e8b6d0374a5f1c9e2b8d4076a";
    private const string Body = """{"id":"evt_123","type":"invoice.paid","data":{"amount":"19.99"}}""";
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_signed_body_verifies_against_the_same_secret()
    {
        var time = new FakeTimeProvider(Now);
        var header = new WebhookSigner(time).Sign(Secret, Body);

        var result = new SignatureVerifier(time).Verify(header.ToHeaderValue(), Body, Secret);

        result.Should().Be(SignatureVerificationResult.Valid);
    }

    [Fact]
    public void The_header_is_formatted_as_t_unix_comma_v1_lowercase_hex()
    {
        var header = new WebhookSigner(new FakeTimeProvider(Now)).Sign(Secret, Body);

        header.Timestamp.Should().Be(Now.ToUnixTimeSeconds());
        header.Signature.Should().MatchRegex("^[0-9a-f]{64}$");
        header.ToHeaderValue().Should().Be($"t={Now.ToUnixTimeSeconds()},v1={header.Signature}");
        SignatureHeader.HeaderName.Should().Be("X-Hooksmith-Signature");
    }

    [Fact]
    public void The_mac_is_computed_over_timestamp_dot_body()
    {
        // Pinned against an independent computation, so a change to the signed-payload
        // format is a test failure rather than a silent break of every subscriber.
        var expected = System.Convert.ToHexStringLower(
            System.Security.Cryptography.HMACSHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Secret),
                System.Text.Encoding.UTF8.GetBytes($"{Now.ToUnixTimeSeconds()}.{Body}")));

        WebhookSigner.ComputeSignature(Secret, Now.ToUnixTimeSeconds(), Body).Should().Be(expected);
        WebhookSigner.BuildSignedPayload(Now.ToUnixTimeSeconds(), Body)
            .Should().Be($"{Now.ToUnixTimeSeconds()}.{Body}");
    }

    [Fact]
    public void A_tampered_body_fails()
    {
        var time = new FakeTimeProvider(Now);
        var header = new WebhookSigner(time).Sign(Secret, Body);
        var tampered = Body.Replace("19.99", "1900.00", StringComparison.Ordinal);

        new SignatureVerifier(time).Verify(header.ToHeaderValue(), tampered, Secret)
            .Should().Be(SignatureVerificationResult.SignatureMismatch);
    }

    [Fact]
    public void A_single_flipped_byte_in_the_body_fails()
    {
        var time = new FakeTimeProvider(Now);
        var header = new WebhookSigner(time).Sign(Secret, Body);

        new SignatureVerifier(time).Verify(header.ToHeaderValue(), Body + " ", Secret)
            .Should().Be(SignatureVerificationResult.SignatureMismatch);
    }

    [Fact]
    public void The_wrong_secret_fails()
    {
        var time = new FakeTimeProvider(Now);
        var header = new WebhookSigner(time).Sign(Secret, Body);

        new SignatureVerifier(time).Verify(header.ToHeaderValue(), Body, Secret + "x")
            .Should().Be(SignatureVerificationResult.SignatureMismatch);
    }

    [Fact]
    public void Rewriting_the_timestamp_to_dodge_expiry_breaks_the_mac()
    {
        // This is the whole reason the timestamp is inside the signed payload. An attacker
        // captures a valid delivery, waits out the tolerance window, and re-sends it with a
        // fresh t=. If the MAC covered only the body, this would verify.
        var time = new FakeTimeProvider(Now);
        var captured = new WebhookSigner(time).Sign(Secret, Body);

        time.Advance(TimeSpan.FromHours(9));
        var replayed = new SignatureHeader(time.GetUtcNow().ToUnixTimeSeconds(), captured.Signature);

        new SignatureVerifier(time).Verify(replayed.ToHeaderValue(), Body, Secret)
            .Should().Be(SignatureVerificationResult.SignatureMismatch);
    }

    [Fact]
    public void An_unmodified_replay_fails_the_freshness_check()
    {
        var time = new FakeTimeProvider(Now);
        var header = new WebhookSigner(time).Sign(Secret, Body);
        var verifier = new SignatureVerifier(time);

        time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        verifier.Verify(header.ToHeaderValue(), Body, Secret)
            .Should().Be(SignatureVerificationResult.Valid, "still inside the five minute window");

        time.Advance(TimeSpan.FromSeconds(2));
        verifier.Verify(header.ToHeaderValue(), Body, Secret)
            .Should().Be(SignatureVerificationResult.TimestampOutsideTolerance);
    }

    [Fact]
    public void A_timestamp_far_in_the_future_is_rejected_too()
    {
        var time = new FakeTimeProvider(Now);
        var future = Now.AddHours(2).ToUnixTimeSeconds();
        var header = new SignatureHeader(future, WebhookSigner.ComputeSignature(Secret, future, Body));

        new SignatureVerifier(time).Verify(header.ToHeaderValue(), Body, Secret)
            .Should().Be(SignatureVerificationResult.TimestampOutsideTolerance);
    }

    [Fact]
    public void The_tolerance_window_is_configurable()
    {
        var time = new FakeTimeProvider(Now);
        var header = new WebhookSigner(time).Sign(Secret, Body);
        var generous = new SignatureVerifier(time, TimeSpan.FromHours(1));

        time.Advance(TimeSpan.FromMinutes(30));

        generous.Verify(header.ToHeaderValue(), Body, Secret).Should().Be(SignatureVerificationResult.Valid);
        new SignatureVerifier(time).Verify(header.ToHeaderValue(), Body, Secret)
            .Should().Be(SignatureVerificationResult.TimestampOutsideTolerance);
    }

    [Fact]
    public void A_forged_signature_reports_mismatch_rather_than_expiry()
    {
        // Verification order matters: an attacker who does not hold the secret should never
        // be told anything about the clock, so the MAC is checked before the timestamp.
        var time = new FakeTimeProvider(Now);
        var stale = Now.AddDays(-30).ToUnixTimeSeconds();
        var forged = new SignatureHeader(stale, new string('a', 64));

        new SignatureVerifier(time).Verify(forged.ToHeaderValue(), Body, Secret)
            .Should().Be(SignatureVerificationResult.SignatureMismatch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("t=1769515200")]
    [InlineData("v1=abc")]
    [InlineData("t=,v1=")]
    [InlineData("t=notanumber,v1=0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("t=-1,v1=0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("t=1769515200,v1=tooshort")]
    [InlineData("t=1769515200,v1=NOTLOWERCASEHEX00000000000000000000000000000000000000000000000000")]
    [InlineData("t=1769515200,v1=zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("t=1769515200,t=1769515900,v1=0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("t=1769515200,v1=0000000000000000000000000000000000000000000000000000000000000000,v1=1111111111111111111111111111111111111111111111111111111111111111")]
    public void Malformed_headers_are_rejected_without_throwing(string? headerValue)
    {
        SignatureHeader.TryParse(headerValue, out _).Should().BeFalse();

        new SignatureVerifier(new FakeTimeProvider(Now)).Verify(headerValue, Body, Secret)
            .Should().Be(SignatureVerificationResult.Malformed);
    }

    [Fact]
    public void Whitespace_and_unknown_schemes_are_tolerated()
    {
        var signature = WebhookSigner.ComputeSignature(Secret, Now.ToUnixTimeSeconds(), Body);
        var headerValue = $" t={Now.ToUnixTimeSeconds()} , v2=somethingfromthefuture , v1={signature} ";

        new SignatureVerifier(new FakeTimeProvider(Now)).Verify(headerValue, Body, Secret)
            .Should().Be(SignatureVerificationResult.Valid);
    }

    [Fact]
    public void IsValid_is_true_only_for_a_valid_fresh_signature()
    {
        var time = new FakeTimeProvider(Now);
        var verifier = new SignatureVerifier(time);
        var header = new WebhookSigner(time).Sign(Secret, Body).ToHeaderValue();

        verifier.IsValid(header, Body, Secret).Should().BeTrue();
        verifier.IsValid(header, Body + "!", Secret).Should().BeFalse();
        verifier.IsValid("nonsense", Body, Secret).Should().BeFalse();
    }

    [Fact]
    public void An_empty_body_still_signs_and_verifies()
    {
        var time = new FakeTimeProvider(Now);
        var header = new WebhookSigner(time).Sign(Secret, string.Empty);

        new SignatureVerifier(time).Verify(header.ToHeaderValue(), string.Empty, Secret)
            .Should().Be(SignatureVerificationResult.Valid);
    }

    [Fact]
    public void Unicode_bodies_round_trip()
    {
        var time = new FakeTimeProvider(Now);
        const string unicodeBody = """{"name":"Müller","emoji":"🪝","zh":"事件"}""";
        var header = new WebhookSigner(time).Sign(Secret, unicodeBody);

        new SignatureVerifier(time).Verify(header.ToHeaderValue(), unicodeBody, Secret)
            .Should().Be(SignatureVerificationResult.Valid);
    }
}
