using Hooksmith.Core;

namespace Hooksmith.Api.Contracts;

/// <summary>One page of results.</summary>
/// <typeparam name="T">The item type.</typeparam>
/// <param name="Items">The items on this page.</param>
/// <param name="Page">The 1-based page number.</param>
/// <param name="PageSize">How many items a full page holds.</param>
/// <param name="TotalCount">How many items match the filter in total.</param>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    /// <summary>How many pages the filter yields.</summary>
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    /// <summary>Whether another page follows this one.</summary>
    public bool HasMore => Page * PageSize < TotalCount;
}

/// <summary>A delivery in list form.</summary>
/// <param name="Id">The delivery id.</param>
/// <param name="EventId">The event being delivered.</param>
/// <param name="EventType">The event's type, joined for display.</param>
/// <param name="SubscriptionId">The target subscription.</param>
/// <param name="SubscriptionName">The subscription's name, joined for display.</param>
/// <param name="Status">Current lifecycle state.</param>
/// <param name="AttemptCount">How many attempts have been started.</param>
/// <param name="NextAttemptAt">When the next attempt is due, if any.</param>
/// <param name="CreatedAt">When the delivery row was created.</param>
public sealed record DeliverySummaryResponse(
    Guid Id,
    Guid EventId,
    string EventType,
    Guid SubscriptionId,
    string SubscriptionName,
    DeliveryStatus Status,
    int AttemptCount,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset CreatedAt);

/// <summary>One attempt in a delivery's timeline.</summary>
/// <param name="Id">The attempt id.</param>
/// <param name="AttemptNumber">1-based ordinal within the delivery.</param>
/// <param name="StatusCode">The subscriber's status code, if a response arrived.</param>
/// <param name="ResponseBody">The truncated response body.</param>
/// <param name="Error">The transport failure, if no response arrived.</param>
/// <param name="DurationMs">How long the attempt took.</param>
/// <param name="AttemptedAt">When the attempt started.</param>
public sealed record AttemptResponse(
    Guid Id,
    int AttemptNumber,
    int? StatusCode,
    string? ResponseBody,
    string? Error,
    long DurationMs,
    DateTimeOffset AttemptedAt)
{
    /// <summary>Projects a domain attempt into its API representation.</summary>
    /// <param name="attempt">The attempt to project.</param>
    /// <returns>The response body.</returns>
    public static AttemptResponse From(Attempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        return new AttemptResponse(
            attempt.Id,
            attempt.AttemptNumber,
            attempt.StatusCode,
            attempt.ResponseBody,
            attempt.Error,
            attempt.DurationMs,
            attempt.AttemptedAt);
    }
}

/// <summary>A delivery with its full attempt timeline and the payload that was sent.</summary>
/// <param name="Delivery">The delivery summary.</param>
/// <param name="Attempts">Every attempt, oldest first.</param>
/// <param name="Payload">The event payload, as delivered.</param>
/// <param name="CanReplay">Whether <c>POST /replay</c> would currently be accepted.</param>
public sealed record DeliveryDetailResponse(
    DeliverySummaryResponse Delivery,
    IReadOnlyList<AttemptResponse> Attempts,
    System.Text.Json.JsonElement Payload,
    bool CanReplay);

/// <summary>The result of replaying a delivery.</summary>
/// <param name="DeliveryId">The newly created delivery.</param>
/// <param name="ReplayedFrom">The delivery it was created from.</param>
/// <param name="EventId">
/// The event, unchanged. A well-behaved receiver deduplicates on this, which is exactly why
/// replay reuses the event rather than creating a new one.
/// </param>
public sealed record ReplayResponse(Guid DeliveryId, Guid ReplayedFrom, Guid EventId);
