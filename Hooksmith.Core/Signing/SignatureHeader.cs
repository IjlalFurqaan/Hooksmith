using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Hooksmith.Core.Signing;

/// <summary>
/// The parsed contents of an <c>X-Hooksmith-Signature</c> header.
/// </summary>
/// <param name="Timestamp">Unix time in seconds at which the payload was signed.</param>
/// <param name="Signature">Lowercase hex HMAC-SHA256 of <c>{timestamp}.{body}</c>.</param>
public sealed record SignatureHeader(long Timestamp, string Signature)
{
    /// <summary>The HTTP header this value travels in.</summary>
    public const string HeaderName = "X-Hooksmith-Signature";

    /// <summary>The scheme prefix identifying the current signature version.</summary>
    public const string SchemeV1 = "v1";

    private const string TimestampKey = "t";

    /// <summary>Renders the header value in <c>t=&lt;unix&gt;,v1=&lt;hex&gt;</c> form.</summary>
    /// <returns>The header value.</returns>
    public string ToHeaderValue() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{TimestampKey}={Timestamp},{SchemeV1}={Signature}");

    /// <summary>
    /// Parses an <c>X-Hooksmith-Signature</c> header value.
    /// </summary>
    /// <param name="headerValue">The raw header value.</param>
    /// <param name="header">The parsed header on success.</param>
    /// <returns><see langword="true"/> if both a timestamp and a v1 signature were present and well-formed.</returns>
    /// <remarks>
    /// Unrecognised elements are ignored rather than rejected, so a future <c>v2=</c> can be
    /// added alongside <c>v1=</c> without breaking subscribers that only know this version.
    /// Everything else is strict: this parses attacker-controlled input.
    /// </remarks>
    public static bool TryParse(string? headerValue, [NotNullWhen(true)] out SignatureHeader? header)
    {
        header = null;

        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return false;
        }

        long? timestamp = null;
        string? signature = null;

        foreach (var range in headerValue.AsSpan().Split(','))
        {
            var element = headerValue.AsSpan()[range].Trim();
            var separator = element.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = element[..separator].Trim();
            var value = element[(separator + 1)..].Trim();

            if (key.SequenceEqual(TimestampKey))
            {
                // A second t= would let an attacker choose which one the verifier reads.
                if (timestamp is not null
                    || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                {
                    return false;
                }

                timestamp = parsed;
            }
            else if (key.SequenceEqual(SchemeV1))
            {
                if (signature is not null || !IsLowercaseHex(value))
                {
                    return false;
                }

                signature = value.ToString();
            }
        }

        if (timestamp is null || signature is null)
        {
            return false;
        }

        header = new SignatureHeader(timestamp.Value, signature);
        return true;
    }

    private static bool IsLowercaseHex(ReadOnlySpan<char> value)
    {
        // HMAC-SHA256 is 32 bytes, so the hex form is always exactly 64 characters.
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}
