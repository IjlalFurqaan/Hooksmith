using Hooksmith.Core;
using Microsoft.AspNetCore.SignalR;

namespace Hooksmith.Api.Realtime;

/// <summary>The payload pushed to dashboard clients when a delivery changes state.</summary>
/// <param name="DeliveryId">The delivery that changed.</param>
/// <param name="SubscriptionId">Which subscription it belongs to.</param>
/// <param name="EventId">Which event it carries.</param>
/// <param name="Status">The new status.</param>
/// <param name="AttemptCount">How many attempts have been started.</param>
/// <param name="NextAttemptAt">When the next attempt is due, if any.</param>
/// <param name="LastStatusCode">The status code of the attempt that caused this change, if any.</param>
/// <param name="LastError">The transport error of the attempt that caused this change, if any.</param>
/// <param name="OccurredAt">When the change happened.</param>
public sealed record DeliveryStatusChanged(
    Guid DeliveryId,
    Guid SubscriptionId,
    Guid EventId,
    DeliveryStatus Status,
    int AttemptCount,
    DateTimeOffset? NextAttemptAt,
    int? LastStatusCode,
    string? LastError,
    DateTimeOffset OccurredAt);

/// <summary>The methods a dashboard client must implement.</summary>
public interface IDeliveriesClient
{
    /// <summary>Called when a delivery the client is watching changes state.</summary>
    /// <param name="change">What changed.</param>
    /// <returns>A task that completes when the client has been notified.</returns>
    Task DeliveryChanged(DeliveryStatusChanged change);
}

/// <summary>
/// The live delivery feed.
/// </summary>
/// <remarks>
/// Clients join a group per subscription rather than receiving everything. On a busy instance
/// the difference is decisive: an operator watching one flaky subscriber should not have their
/// browser hosed by every delivery in the system. Clients that genuinely want the firehose join
/// <see cref="AllDeliveriesGroup"/> explicitly.
/// </remarks>
public sealed class DeliveriesHub : Hub<IDeliveriesClient>
{
    /// <summary>The route the hub is mapped at.</summary>
    public const string Route = "/hubs/deliveries";

    /// <summary>The group receiving every delivery, regardless of subscription.</summary>
    public const string AllDeliveriesGroup = "deliveries:all";

    /// <summary>Subscribes the caller to one subscription's deliveries.</summary>
    /// <param name="subscriptionId">The subscription to watch.</param>
    /// <returns>A task that completes once the caller has joined the group.</returns>
    public Task Watch(Guid subscriptionId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(subscriptionId));

    /// <summary>Unsubscribes the caller from one subscription's deliveries.</summary>
    /// <param name="subscriptionId">The subscription to stop watching.</param>
    /// <returns>A task that completes once the caller has left the group.</returns>
    public Task Unwatch(Guid subscriptionId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(subscriptionId));

    /// <summary>Subscribes the caller to every delivery.</summary>
    /// <returns>A task that completes once the caller has joined the group.</returns>
    public Task WatchAll() => Groups.AddToGroupAsync(Context.ConnectionId, AllDeliveriesGroup);

    /// <summary>Unsubscribes the caller from the firehose.</summary>
    /// <returns>A task that completes once the caller has left the group.</returns>
    public Task UnwatchAll() => Groups.RemoveFromGroupAsync(Context.ConnectionId, AllDeliveriesGroup);

    /// <summary>The group name carrying one subscription's deliveries.</summary>
    /// <param name="subscriptionId">The subscription id.</param>
    /// <returns>The group name.</returns>
    public static string GroupFor(Guid subscriptionId) => $"subscription:{subscriptionId}";
}
