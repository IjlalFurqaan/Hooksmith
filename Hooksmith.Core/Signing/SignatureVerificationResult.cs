namespace Hooksmith.Core.Signing;

/// <summary>
/// The outcome of verifying an <c>X-Hooksmith-Signature</c> header.
/// </summary>
public enum SignatureVerificationResult
{
    /// <summary>Signature matched and the timestamp was inside the tolerance window.</summary>
    Valid = 0,

    /// <summary>The header was missing, or not in <c>t=&lt;unix&gt;,v1=&lt;hex&gt;</c> form.</summary>
    Malformed = 1,

    /// <summary>The MAC did not match: wrong secret, or the body or timestamp was altered.</summary>
    SignatureMismatch = 2,

    /// <summary>
    /// The MAC matched but the timestamp fell outside the tolerance window — an authentic
    /// message being replayed, or a badly skewed clock.
    /// </summary>
    TimestampOutsideTolerance = 3,
}
