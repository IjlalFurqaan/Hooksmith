namespace Hooksmith.Core;

/// <summary>
/// One event's journey to one subscriber. Fan-out creates a delivery per matching
/// subscription; the delivery carries the retry state for that pair.
/// </summary>
/// <param name="Id">Stable identifier for this delivery.</param>
/// <param name="EventId">The <see cref="Event"/> being delivered.</param>
/// <param name="SubscriptionId">The <see cref="Subscription"/> being delivered to.</param>
/// <param name="Status">Current lifecycle state.</param>
/// <param name="AttemptCount">
/// How many attempts have been started. Incremented when the delivery is claimed, not when
/// the attempt completes.
/// </param>
/// <param name="NextAttemptAt">
/// When the worker may next claim this delivery. Non-null exactly when the delivery is
/// waiting to be dispatched (<see cref="DeliveryStatus.Pending"/> or
/// <see cref="DeliveryStatus.Retrying"/>); null otherwise.
/// </param>
/// <param name="CreatedAt">When the delivery row was created by fan-out or replay.</param>
public sealed record Delivery(
    Guid Id,
    Guid EventId,
    Guid SubscriptionId,
    DeliveryStatus Status,
    int AttemptCount,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset CreatedAt)
{
    /// <summary>
    /// Creates a delivery in <see cref="DeliveryStatus.Pending"/>, due immediately.
    /// </summary>
    /// <param name="eventId">The event to deliver.</param>
    /// <param name="subscriptionId">The subscription to deliver to.</param>
    /// <param name="timeProvider">Clock used for <c>CreatedAt</c> and the initial due time.</param>
    /// <returns>A new pending delivery.</returns>
    public static Delivery Create(Guid eventId, Guid subscriptionId, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        var now = timeProvider.GetUtcNow();
        return new Delivery(
            Id: Guid.NewGuid(),
            EventId: eventId,
            SubscriptionId: subscriptionId,
            Status: DeliveryStatus.Pending,
            AttemptCount: 0,
            NextAttemptAt: now,
            CreatedAt: now);
    }

    /// <summary>
    /// Moves the delivery to <paramref name="next"/>, enforcing
    /// <see cref="DeliveryStatusTransitions"/>.
    /// </summary>
    /// <param name="next">The status to move to.</param>
    /// <param name="nextAttemptAt">The new due time, or null to clear it.</param>
    /// <returns>A new delivery record in the requested state.</returns>
    /// <exception cref="InvalidDeliveryTransitionException">The transition is illegal.</exception>
    /// <remarks>
    /// Every state change goes through this method. The semantic helpers below are
    /// conveniences over it, not alternative routes around it.
    /// </remarks>
    public Delivery TransitionTo(DeliveryStatus next, DateTimeOffset? nextAttemptAt = null)
    {
        DeliveryStatusTransitions.EnsureLegal(Status, next);
        return this with { Status = next, NextAttemptAt = nextAttemptAt };
    }

    /// <summary>
    /// Claims the delivery for dispatch: moves it to <see cref="DeliveryStatus.InFlight"/>,
    /// increments <see cref="AttemptCount"/> and clears the due time.
    /// </summary>
    /// <returns>The claimed delivery.</returns>
    /// <exception cref="InvalidDeliveryTransitionException">The delivery was not awaiting dispatch.</exception>
    /// <remarks>
    /// The attempt is counted here, before the HTTP call, rather than after it. If a worker
    /// process dies mid-flight the count has already moved, so a crash-looping delivery
    /// exhausts its budget and dead-letters instead of retrying forever. Counting on
    /// completion would make a crash free, which is the wrong direction to fail in.
    /// </remarks>
    public Delivery Claim() =>
        TransitionTo(DeliveryStatus.InFlight) with { AttemptCount = AttemptCount + 1 };

    /// <summary>Records a 2xx response. Terminal.</summary>
    /// <returns>The succeeded delivery.</returns>
    /// <exception cref="InvalidDeliveryTransitionException">The delivery was not in flight.</exception>
    public Delivery Succeed() => TransitionTo(DeliveryStatus.Succeeded);

    /// <summary>Schedules another attempt after a retryable failure.</summary>
    /// <param name="nextAttemptAt">When the delivery becomes claimable again.</param>
    /// <returns>The retrying delivery.</returns>
    /// <exception cref="InvalidDeliveryTransitionException">The delivery was not in flight.</exception>
    public Delivery ScheduleRetry(DateTimeOffset nextAttemptAt) =>
        TransitionTo(DeliveryStatus.Retrying, nextAttemptAt);

    /// <summary>Abandons the delivery, either on a permanent failure or an exhausted budget.</summary>
    /// <returns>The dead-lettered delivery.</returns>
    /// <exception cref="InvalidDeliveryTransitionException">The delivery was not in flight.</exception>
    public Delivery DeadLetter() => TransitionTo(DeliveryStatus.DeadLettered);

    /// <summary>
    /// Returns a dead-lettered delivery to <see cref="DeliveryStatus.Pending"/>.
    /// </summary>
    /// <param name="timeProvider">Clock used for the new due time.</param>
    /// <returns>The requeued delivery, due immediately.</returns>
    /// <exception cref="InvalidDeliveryTransitionException">The delivery was not dead-lettered.</exception>
    /// <remarks>
    /// This is the <c>DeadLettered -&gt; Pending</c> edge of the transition table. It requeues
    /// the existing row in place and therefore keeps accumulating attempts against the same
    /// delivery. The replay API takes the other route: it creates a fresh delivery for the
    /// same event, so the original attempt history is preserved rather than extended.
    /// </remarks>
    public Delivery RestoreForReplay(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return TransitionTo(DeliveryStatus.Pending, timeProvider.GetUtcNow());
    }

    /// <summary>Whether the delivery is due for dispatch at the given moment.</summary>
    /// <param name="asOf">The moment to evaluate against.</param>
    /// <returns><see langword="true"/> if the worker may claim it now.</returns>
    public bool IsDueAt(DateTimeOffset asOf) =>
        Status is DeliveryStatus.Pending or DeliveryStatus.Retrying
        && NextAttemptAt is { } due
        && due <= asOf;
}
