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
/// When this delivery next becomes actionable, or null for the terminal states.
/// <para>For <see cref="DeliveryStatus.Pending"/> and <see cref="DeliveryStatus.Retrying"/>
/// that means "do not dispatch before this". For <see cref="DeliveryStatus.InFlight"/> it
/// means "if this time passes and the delivery is still in flight, the worker holding it
/// died — take it back". Overloading one column for both lets a single index
/// <c>(status, next_attempt_at)</c> serve dispatching and recovery alike.</para>
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
    /// Claims the delivery for dispatch: moves it to <see cref="DeliveryStatus.InFlight"/> and
    /// increments <see cref="AttemptCount"/>.
    /// </summary>
    /// <param name="reclaimAfter">
    /// The moment after which another worker may assume this attempt was lost and take the
    /// delivery back. Should comfortably exceed the dispatch timeout.
    /// </param>
    /// <returns>The claimed delivery.</returns>
    /// <exception cref="InvalidDeliveryTransitionException">The delivery was not awaiting dispatch.</exception>
    /// <remarks>
    /// <para>The attempt is counted here, before the HTTP call, rather than after it. If a worker
    /// process dies mid-flight the count has already moved, so a crash-looping delivery
    /// exhausts its budget and dead-letters instead of retrying forever. Counting on
    /// completion would make a crash free, which is the wrong direction to fail in.</para>
    /// <para><paramref name="reclaimAfter"/> is the other half of that bargain: counting the
    /// attempt early is only safe if a delivery whose worker died is eventually recovered
    /// rather than left in flight forever.</para>
    /// </remarks>
    public Delivery Claim(DateTimeOffset reclaimAfter) =>
        TransitionTo(DeliveryStatus.InFlight, reclaimAfter) with { AttemptCount = AttemptCount + 1 };

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

    /// <summary>
    /// Returns an in-flight delivery whose worker appears to have died to
    /// <see cref="DeliveryStatus.Retrying"/>, due immediately.
    /// </summary>
    /// <param name="timeProvider">Clock used for the new due time.</param>
    /// <returns>The recovered delivery.</returns>
    /// <exception cref="InvalidDeliveryTransitionException">The delivery was not in flight.</exception>
    /// <remarks>
    /// The spent attempt is not refunded. If the worker died after the subscriber had already
    /// processed the request, refunding it would turn one crash into an unbounded redelivery
    /// loop against a receiver that was working fine.
    /// </remarks>
    public Delivery ReclaimAfterWorkerLoss(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return TransitionTo(DeliveryStatus.Retrying, timeProvider.GetUtcNow());
    }

    /// <summary>Whether the delivery is due for dispatch at the given moment.</summary>
    /// <param name="asOf">The moment to evaluate against.</param>
    /// <returns><see langword="true"/> if the worker may claim it now.</returns>
    public bool IsDueAt(DateTimeOffset asOf) =>
        Status is DeliveryStatus.Pending or DeliveryStatus.Retrying
        && NextAttemptAt is { } due
        && due <= asOf;

    /// <summary>Whether an in-flight delivery has outlived its reclaim deadline.</summary>
    /// <param name="asOf">The moment to evaluate against.</param>
    /// <returns><see langword="true"/> if the worker holding it should be assumed dead.</returns>
    public bool IsAbandonedAt(DateTimeOffset asOf) =>
        Status is DeliveryStatus.InFlight && NextAttemptAt is { } deadline && deadline <= asOf;
}
