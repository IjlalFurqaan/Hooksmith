using System.Text;

namespace Hooksmith.Core;

/// <summary>
/// The immutable record of one dispatch attempt against a <see cref="Delivery"/>.
/// Attempts are append-only; they are the audit trail the dashboard renders.
/// </summary>
/// <param name="Id">Stable identifier for this attempt.</param>
/// <param name="DeliveryId">The delivery this attempt belongs to.</param>
/// <param name="AttemptNumber">1-based ordinal within the delivery.</param>
/// <param name="StatusCode">The subscriber's HTTP status code, or null if no response arrived.</param>
/// <param name="ResponseBody">
/// The subscriber's response body, truncated to <see cref="MaxResponseBodyBytes"/>.
/// </param>
/// <param name="Error">Transport-level failure detail (timeout, DNS, TLS), or null on an HTTP response.</param>
/// <param name="DurationMs">Wall-clock duration of the attempt in milliseconds.</param>
/// <param name="AttemptedAt">When the attempt started.</param>
public sealed record Attempt(
    Guid Id,
    Guid DeliveryId,
    int AttemptNumber,
    int? StatusCode,
    string? ResponseBody,
    string? Error,
    long DurationMs,
    DateTimeOffset AttemptedAt)
{
    /// <summary>
    /// Maximum stored size of <see cref="ResponseBody"/>, in UTF-8 bytes.
    /// </summary>
    /// <remarks>
    /// A subscriber that answers a webhook with a 5 MB HTML error page should not be able to
    /// fill our database one failed attempt at a time. Two kilobytes is enough to read a stack
    /// trace or an error envelope, which is all this field is for.
    /// </remarks>
    public const int MaxResponseBodyBytes = 2048;

    /// <summary>
    /// Creates an attempt record, truncating the response body to
    /// <see cref="MaxResponseBodyBytes"/>.
    /// </summary>
    /// <param name="deliveryId">The delivery this attempt belongs to.</param>
    /// <param name="attemptNumber">1-based ordinal within the delivery.</param>
    /// <param name="statusCode">The subscriber's HTTP status code, if a response arrived.</param>
    /// <param name="responseBody">The raw response body, truncated on the way in.</param>
    /// <param name="error">Transport-level failure detail, if the request never completed.</param>
    /// <param name="durationMs">Wall-clock duration in milliseconds.</param>
    /// <param name="attemptedAt">When the attempt started.</param>
    /// <returns>A new attempt record.</returns>
    public static Attempt Create(
        Guid deliveryId,
        int attemptNumber,
        int? statusCode,
        string? responseBody,
        string? error,
        long durationMs,
        DateTimeOffset attemptedAt) =>
        new(
            Id: Guid.NewGuid(),
            DeliveryId: deliveryId,
            AttemptNumber: attemptNumber,
            StatusCode: statusCode,
            ResponseBody: TruncateResponseBody(responseBody),
            Error: error,
            DurationMs: durationMs,
            AttemptedAt: attemptedAt);

    /// <summary>
    /// Truncates a response body to at most <see cref="MaxResponseBodyBytes"/> UTF-8 bytes.
    /// </summary>
    /// <param name="body">The body to truncate; null passes through.</param>
    /// <returns>The body, cut at a character boundary if it was over budget.</returns>
    /// <remarks>
    /// The cut is made on a character boundary, never mid-surrogate-pair, so the stored text
    /// stays valid UTF-16 and round-trips through JSON without producing replacement characters.
    /// </remarks>
    public static string? TruncateResponseBody(string? body)
    {
        if (body is null || Encoding.UTF8.GetByteCount(body) <= MaxResponseBodyBytes)
        {
            return body;
        }

        var buffer = new byte[MaxResponseBodyBytes];
        Encoding.UTF8.GetEncoder().Convert(
            body.AsSpan(),
            buffer,
            flush: true,
            out var charsUsed,
            out _,
            out _);

        return body[..charsUsed];
    }
}
