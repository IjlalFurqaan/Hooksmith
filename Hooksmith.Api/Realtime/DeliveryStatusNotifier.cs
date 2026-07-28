using Hooksmith.Api.Logging;
using Hooksmith.Core;
using Microsoft.AspNetCore.SignalR;

namespace Hooksmith.Api.Realtime;

/// <summary>Broadcasts delivery status changes to dashboard clients.</summary>
/// <remarks>
/// An interface rather than a direct <see cref="IHubContext{THub}"/> dependency so the delivery
/// pipeline can be integration-tested without a SignalR host, and so a broadcast failure can
/// never be the reason a delivery fails to record.
/// </remarks>
public interface IDeliveryStatusNotifier
{
    /// <summary>Announces that a delivery changed state.</summary>
    /// <param name="delivery">The delivery in its new state.</param>
    /// <param name="attempt">The attempt that caused the change, if there was one.</param>
    /// <param name="cancellationToken">Cancels the broadcast.</param>
    /// <returns>A task that completes when the broadcast has been handed off.</returns>
    Task DeliveryChangedAsync(Delivery delivery, Attempt? attempt, CancellationToken cancellationToken = default);
}

/// <summary>The SignalR implementation of <see cref="IDeliveryStatusNotifier"/>.</summary>
public sealed class SignalRDeliveryStatusNotifier : IDeliveryStatusNotifier
{
    private readonly IHubContext<DeliveriesHub, IDeliveriesClient> _hub;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SignalRDeliveryStatusNotifier> _logger;

    /// <summary>Initialises the notifier.</summary>
    /// <param name="hub">The hub context to broadcast through.</param>
    /// <param name="timeProvider">Clock stamping the change.</param>
    /// <param name="logger">Log sink.</param>
    public SignalRDeliveryStatusNotifier(
        IHubContext<DeliveriesHub, IDeliveriesClient> hub,
        TimeProvider timeProvider,
        ILogger<SignalRDeliveryStatusNotifier> logger)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _hub = hub;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task DeliveryChangedAsync(
        Delivery delivery,
        Attempt? attempt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        var change = new DeliveryStatusChanged(
            DeliveryId: delivery.Id,
            SubscriptionId: delivery.SubscriptionId,
            EventId: delivery.EventId,
            Status: delivery.Status,
            AttemptCount: delivery.AttemptCount,
            NextAttemptAt: delivery.NextAttemptAt,
            LastStatusCode: attempt?.StatusCode,
            LastError: attempt?.Error,
            OccurredAt: _timeProvider.GetUtcNow());

        try
        {
            await Task.WhenAll(
                    _hub.Clients.Group(DeliveriesHub.GroupFor(delivery.SubscriptionId)).DeliveryChanged(change),
                    _hub.Clients.Group(DeliveriesHub.AllDeliveriesGroup).DeliveryChanged(change))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The delivery is already committed. A dashboard that misses a frame is a cosmetic
            // problem; throwing here would turn it into a failed delivery.
            RealtimeLog.BroadcastFailed(_logger, delivery.Id, ex);
        }
    }
}

/// <summary>A notifier that does nothing, for tests and for hosts without a hub.</summary>
public sealed class NullDeliveryStatusNotifier : IDeliveryStatusNotifier
{
    /// <inheritdoc />
    public Task DeliveryChangedAsync(
        Delivery delivery,
        Attempt? attempt,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
