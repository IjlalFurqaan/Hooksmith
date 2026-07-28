namespace Hooksmith.Infrastructure.Dispatch;

/// <summary>Everything the dispatcher needs to make one attempt.</summary>
/// <param name="DeliveryId">The delivery being attempted, sent as a header for correlation.</param>
/// <param name="EventId">The event id, sent as a header and present in the body.</param>
/// <param name="EventType">The event type, sent as a header.</param>
/// <param name="AttemptNumber">1-based attempt ordinal, sent as a header.</param>
/// <param name="Url">The subscriber endpoint.</param>
/// <param name="Secret">The subscription's signing secret.</param>
/// <param name="Body">The exact serialised envelope. Signed as-is and sent as-is.</param>
public sealed record DispatchRequest(
    Guid DeliveryId,
    Guid EventId,
    string EventType,
    int AttemptNumber,
    Uri Url,
    string Secret,
    string Body);

/// <summary>The raw result of one attempt, before it is classified.</summary>
/// <param name="StatusCode">The subscriber's status code, or null if no response arrived.</param>
/// <param name="ResponseBody">The response body, already truncated.</param>
/// <param name="Error">Why no response arrived, or null on an HTTP response.</param>
/// <param name="Duration">Wall-clock duration of the attempt.</param>
/// <param name="RetryAfter">
/// A usable <c>Retry-After</c> from the subscriber, already converted to a delay. Null when
/// absent or unparseable.
/// </param>
/// <remarks>
/// This type deliberately does not decide anything. Turning a status code into
/// <see cref="Hooksmith.Core.DispatchOutcome"/> is policy and lives in Core; the dispatcher's
/// job is to report faithfully what happened on the wire.
/// </remarks>
public sealed record DispatchResult(
    int? StatusCode,
    string? ResponseBody,
    string? Error,
    TimeSpan Duration,
    TimeSpan? RetryAfter)
{
    /// <summary>Whether an HTTP response arrived at all.</summary>
    public bool HasResponse => StatusCode is not null;
}

/// <summary>Sends one webhook attempt.</summary>
public interface IWebhookDispatcher
{
    /// <summary>Makes a single attempt against a subscriber.</summary>
    /// <param name="request">What to send and where.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <returns>What happened. Transport failures are reported, not thrown.</returns>
    Task<DispatchResult> DispatchAsync(DispatchRequest request, CancellationToken cancellationToken = default);
}
