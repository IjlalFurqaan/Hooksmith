using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Hooksmith.Core.Signing;

/// <summary>
/// Computes the <c>X-Hooksmith-Signature</c> header for an outbound delivery.
/// </summary>
/// <remarks>
/// <para>
/// The MAC is HMAC-SHA256 over <c>{timestamp}.{body}</c> — the timestamp is <em>inside</em>
/// the signed material, not merely transported next to it. If the header were
/// <c>t=&lt;unix&gt;</c> alongside an HMAC of the body alone, then a captured request could be
/// resent tomorrow with <c>t</c> rewritten to the current time: the signature would still
/// verify, because the signature never covered the timestamp. Freshness checks would then be
/// checking a field the attacker controls. Signing the timestamp binds the body to the moment
/// it was sent, so a replayed request either carries its original timestamp and fails the
/// tolerance window, or carries a rewritten one and fails the MAC.
/// </para>
/// <para>The body is signed as its exact UTF-8 bytes. It must be the same bytes put on the
/// wire — re-serialising JSON before signing, or after, is how signatures start failing in
/// production for no visible reason.</para>
/// </remarks>
public sealed class WebhookSigner
{
    private readonly TimeProvider _timeProvider;

    /// <summary>Initialises the signer.</summary>
    /// <param name="timeProvider">Clock supplying the signing timestamp.</param>
    public WebhookSigner(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <summary>Signs a body with the subscription secret, stamped with the current time.</summary>
    /// <param name="secret">The subscription's shared secret.</param>
    /// <param name="body">The exact request body that will be sent.</param>
    /// <returns>The header to attach to the request.</returns>
    public SignatureHeader Sign(string secret, string body)
    {
        var timestamp = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        return new SignatureHeader(timestamp, ComputeSignature(secret, timestamp, body));
    }

    /// <summary>
    /// Computes the lowercase hex HMAC-SHA256 of <c>{timestamp}.{body}</c>.
    /// </summary>
    /// <param name="secret">The shared secret.</param>
    /// <param name="timestamp">Unix time in seconds.</param>
    /// <param name="body">The request body.</param>
    /// <returns>64 lowercase hex characters.</returns>
    public static string ComputeSignature(string secret, long timestamp, string body)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(body);

        var key = Encoding.UTF8.GetBytes(secret);
        var signedPayload = Encoding.UTF8.GetBytes(BuildSignedPayload(timestamp, body));

        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(key, signedPayload, mac);

        return Convert.ToHexStringLower(mac);
    }

    /// <summary>Builds the exact string the MAC is computed over.</summary>
    /// <param name="timestamp">Unix time in seconds.</param>
    /// <param name="body">The request body.</param>
    /// <returns>The signed payload, <c>{timestamp}.{body}</c>.</returns>
    public static string BuildSignedPayload(long timestamp, string body) =>
        string.Create(CultureInfo.InvariantCulture, $"{timestamp}.{body}");
}
