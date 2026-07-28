using Hooksmith.Api.Logging;
using Hooksmith.Api.Realtime;
using Hooksmith.Core;
using Hooksmith.Core.Retry;
using Hooksmith.Infrastructure.Dispatch;
using Hooksmith.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hooksmith.Api.Workers;

/// <summary>
/// Dispatches one claimed delivery and records the outcome.
/// </summary>
/// <remarks>
/// Deliberately separate from the hosted <see cref="DeliveryWorker"/> loop. The loop is about
/// claiming, concurrency and shutdown; this is about what happens to a single delivery. Keeping
/// them apart is what lets an integration test drive dispatch deterministically without
/// starting a background service and waiting on a timer.
/// </remarks>
public sealed class DeliveryProcessor
{
    private readonly HooksmithDbContext _db;
    private readonly IWebhookDispatcher _dispatcher;
    private readonly RetryPolicy _retryPolicy;
    private readonly IDeliveryStatusNotifier _notifier;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DeliveryProcessor> _logger;

    /// <summary>Initialises the processor.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="dispatcher">Sends the HTTP request.</param>
    /// <param name="retryPolicy">Decides whether and when to try again.</param>
    /// <param name="notifier">Broadcasts status changes to dashboard clients.</param>
    /// <param name="timeProvider">Clock for attempt timestamps.</param>
    /// <param name="logger">Log sink.</param>
    public DeliveryProcessor(
        HooksmithDbContext db,
        IWebhookDispatcher dispatcher,
        RetryPolicy retryPolicy,
        IDeliveryStatusNotifier notifier,
        TimeProvider timeProvider,
        ILogger<DeliveryProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(retryPolicy);
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _db = db;
        _dispatcher = dispatcher;
        _retryPolicy = retryPolicy;
        _notifier = notifier;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Dispatches a claimed delivery, writes its attempt record, and moves it to its next state.
    /// </summary>
    /// <param name="deliveryId">The delivery, already claimed and in flight.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The delivery's status after the attempt, or null if it could not be processed.</returns>
    public async Task<DeliveryStatus?> ProcessAsync(Guid deliveryId, CancellationToken cancellationToken = default)
    {
        var delivery = await _db.Deliveries
            .SingleOrDefaultAsync(d => d.Id == deliveryId, cancellationToken)
            .ConfigureAwait(false);

        if (delivery is null)
        {
            return null;
        }

        if (delivery.Status is not DeliveryStatus.InFlight)
        {
            // Someone else finished it, or the reclaim sweep took it back. Either way it is not
            // ours to send, and re-sending it would be the double delivery SKIP LOCKED exists
            // to prevent.
            DeliveryLog.SkippedNotInFlight(_logger, deliveryId, delivery.Status);
            return delivery.Status;
        }

        var context = await LoadContextAsync(delivery, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            // The subscription or event was deleted between fan-out and dispatch. There is
            // nothing to send and nothing to retry.
            var orphaned = delivery.DeadLetter();
            await SaveAsync(delivery, orphaned, attempt: null, cancellationToken).ConfigureAwait(false);
            DeliveryLog.Orphaned(_logger, deliveryId);
            return orphaned.Status;
        }

        var (subscription, @event) = context.Value;
        var startedAt = _timeProvider.GetUtcNow();

        var result = await _dispatcher.DispatchAsync(
                new DispatchRequest(
                    DeliveryId: delivery.Id,
                    EventId: @event.Id,
                    EventType: @event.Type,
                    AttemptNumber: delivery.AttemptCount,
                    Url: subscription.Url,
                    Secret: subscription.Secret,
                    Body: DeliveryEnvelope.Serialise(@event)),
                cancellationToken)
            .ConfigureAwait(false);

        var attempt = Attempt.Create(
            deliveryId: delivery.Id,
            attemptNumber: delivery.AttemptCount,
            statusCode: result.StatusCode,
            responseBody: result.ResponseBody,
            error: result.Error,
            durationMs: (long)result.Duration.TotalMilliseconds,
            attemptedAt: startedAt);

        var next = Decide(delivery, result);
        await SaveAsync(delivery, next, attempt, cancellationToken).ConfigureAwait(false);

        DeliveryLog.Attempted(
            _logger,
            delivery.Id,
            delivery.AttemptCount,
            result.StatusCode,
            next.Status,
            (long)result.Duration.TotalMilliseconds);

        return next.Status;
    }

    /// <summary>
    /// Turns a dispatch result into the delivery's next state.
    /// </summary>
    /// <remarks>
    /// The attempt budget outranks everything. A subscriber that answers 429 with a polite
    /// <c>Retry-After</c> on its sixth failure still gets dead-lettered — the header influences
    /// <em>when</em> the next attempt happens, never <em>whether</em> there is one.
    /// </remarks>
    private Delivery Decide(Delivery delivery, DispatchResult result)
    {
        var outcome = result.StatusCode is { } statusCode
            ? DispatchOutcomePolicy.Classify(statusCode)
            : DispatchOutcomePolicy.ForTransportFailure();

        if (outcome is DispatchOutcome.Succeeded)
        {
            return delivery.Succeed();
        }

        if (outcome is DispatchOutcome.Permanent)
        {
            return delivery.DeadLetter();
        }

        return _retryPolicy.TryGetNextAttemptTime(delivery.AttemptCount, result.RetryAfter, out var nextAttemptAt)
            ? delivery.ScheduleRetry(nextAttemptAt)
            : delivery.DeadLetter();
    }

    private async Task<(Subscription Subscription, Event Event)?> LoadContextAsync(
        Delivery delivery,
        CancellationToken cancellationToken)
    {
        var subscription = await _db.Subscriptions
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == delivery.SubscriptionId, cancellationToken)
            .ConfigureAwait(false);

        if (subscription is null)
        {
            return null;
        }

        var @event = await _db.Events
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == delivery.EventId, cancellationToken)
            .ConfigureAwait(false);

        return @event is null ? null : (subscription, @event);
    }

    private async Task SaveAsync(
        Delivery tracked,
        Delivery next,
        Attempt? attempt,
        CancellationToken cancellationToken)
    {
        _db.Entry(tracked).CurrentValues.SetValues(next);

        if (attempt is not null)
        {
            _db.Attempts.Add(attempt);
        }

        // The attempt record and the status change land together. An attempt without the status
        // move would re-send; a status move without the attempt would lose the audit trail.
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _notifier.DeliveryChangedAsync(next, attempt, cancellationToken).ConfigureAwait(false);
    }
}
