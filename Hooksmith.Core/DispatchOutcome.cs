namespace Hooksmith.Core;

/// <summary>
/// What a single dispatch attempt means for the delivery's future.
/// </summary>
public enum DispatchOutcome
{
    /// <summary>The subscriber accepted it. Nothing further to do.</summary>
    Succeeded = 0,

    /// <summary>Worth trying again, budget permitting.</summary>
    Retry = 1,

    /// <summary>Will not improve on retry. Abandon immediately.</summary>
    Permanent = 2,
}

/// <summary>
/// Maps a dispatch result onto a <see cref="DispatchOutcome"/>.
/// </summary>
/// <remarks>
/// Pure policy, kept in Core so it can be unit tested across the whole status-code space
/// without an HTTP stack in sight.
/// </remarks>
public static class DispatchOutcomePolicy
{
    /// <summary>Classifies an HTTP status code returned by a subscriber.</summary>
    /// <param name="statusCode">The status code the subscriber answered with.</param>
    /// <returns>What the delivery should do next.</returns>
    /// <remarks>
    /// <para>2xx succeeds. 408 and 429 retry because they explicitly mean "not now". 5xx retries
    /// because the subscriber is broken rather than the request.</para>
    /// <para>Every other 4xx dead-letters immediately: a 400, 401 or 404 from a subscriber will
    /// still be a 400, 401 or 404 in thirty seconds, and spending six attempts to confirm that
    /// only delays the operator finding out.</para>
    /// <para>1xx and 3xx also dead-letter. A webhook receiver answering with a redirect we have
    /// already refused to follow, or an informational status, is misconfigured, and
    /// misconfiguration does not resolve itself on a timer.</para>
    /// </remarks>
    public static DispatchOutcome Classify(int statusCode) => statusCode switch
    {
        >= 200 and <= 299 => DispatchOutcome.Succeeded,
        408 or 429 => DispatchOutcome.Retry,
        >= 500 and <= 599 => DispatchOutcome.Retry,
        _ => DispatchOutcome.Permanent,
    };

    /// <summary>
    /// Classifies a failure where no HTTP response arrived at all.
    /// </summary>
    /// <returns>Always <see cref="DispatchOutcome.Retry"/>.</returns>
    /// <remarks>
    /// Timeouts, DNS failures, connection resets and TLS handshake errors are all conditions
    /// that a subscriber can recover from, so they consume an attempt rather than the whole
    /// delivery. The attempt budget is what stops this looping forever.
    /// </remarks>
    public static DispatchOutcome ForTransportFailure() => DispatchOutcome.Retry;
}
