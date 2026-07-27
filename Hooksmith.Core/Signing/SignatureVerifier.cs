using System.Security.Cryptography;

namespace Hooksmith.Core.Signing;

/// <summary>
/// Verifies an <c>X-Hooksmith-Signature</c> header against a body and secret.
/// </summary>
/// <remarks>
/// <para>
/// This type exists to be handed to subscribers, not only to be used internally: the
/// receiving side of a webhook needs exactly this logic, and every subscriber that has to
/// reimplement it is a subscriber that will get it subtly wrong. It lives in
/// <c>Hooksmith.Core</c> and depends on nothing but the BCL, so it can be referenced from
/// any .NET application.
/// </para>
/// <para>
/// Verification order is deliberate: parse, then compare the MAC in constant time, then
/// check freshness. Reporting <see cref="SignatureVerificationResult.TimestampOutsideTolerance"/>
/// only for messages whose MAC already verified means an attacker with a forged signature
/// learns nothing about the clock from the answer they get back.
/// </para>
/// </remarks>
public sealed class SignatureVerifier
{
    /// <summary>How far the signing timestamp may drift from now before the message is rejected.</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _tolerance;

    /// <summary>Initialises the verifier.</summary>
    /// <param name="timeProvider">Clock used to evaluate freshness.</param>
    /// <param name="tolerance">
    /// Maximum accepted clock difference in either direction. Defaults to
    /// <see cref="DefaultTolerance"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tolerance"/> is negative.</exception>
    public SignatureVerifier(TimeProvider timeProvider, TimeSpan? tolerance = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(tolerance ?? DefaultTolerance, TimeSpan.Zero);

        _timeProvider = timeProvider;
        _tolerance = tolerance ?? DefaultTolerance;
    }

    /// <summary>Verifies a received webhook.</summary>
    /// <param name="headerValue">The raw <c>X-Hooksmith-Signature</c> header value.</param>
    /// <param name="body">The exact request body as received, before any deserialisation.</param>
    /// <param name="secret">The shared secret for the subscription.</param>
    /// <returns>Why the signature was accepted or rejected.</returns>
    /// <remarks>
    /// The tolerance is applied symmetrically. A timestamp far in the past is a replay; a
    /// timestamp far in the future is a broken or manipulated clock. Neither is a message
    /// worth trusting, and accepting the future case would let one skewed sender mint
    /// deliveries that stay valid indefinitely.
    /// </remarks>
    public SignatureVerificationResult Verify(string? headerValue, string body, string secret)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(secret);

        if (!SignatureHeader.TryParse(headerValue, out var header))
        {
            return SignatureVerificationResult.Malformed;
        }

        var expected = WebhookSigner.ComputeSignature(secret, header.Timestamp, body);

        // Both sides are 64 chars of validated lowercase hex, so the lengths always match
        // and FixedTimeEquals does the comparison without leaking the position of the first
        // differing byte through timing.
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expected),
                Convert.FromHexString(header.Signature)))
        {
            return SignatureVerificationResult.SignatureMismatch;
        }

        var age = _timeProvider.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(header.Timestamp);
        return age.Duration() > _tolerance
            ? SignatureVerificationResult.TimestampOutsideTolerance
            : SignatureVerificationResult.Valid;
    }

    /// <summary>Verifies a received webhook, discarding the reason for rejection.</summary>
    /// <param name="headerValue">The raw <c>X-Hooksmith-Signature</c> header value.</param>
    /// <param name="body">The exact request body as received.</param>
    /// <param name="secret">The shared secret for the subscription.</param>
    /// <returns><see langword="true"/> only if the signature is valid and fresh.</returns>
    public bool IsValid(string? headerValue, string body, string secret) =>
        Verify(headerValue, body, secret) == SignatureVerificationResult.Valid;
}
