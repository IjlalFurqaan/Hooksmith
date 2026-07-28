using Hooksmith.Core;

namespace Hooksmith.Api.Logging;

/// <summary>Source-generated log messages for the delivery pipeline.</summary>
internal static partial class DeliveryLog
{
    [LoggerMessage(
        EventId = 2000,
        Level = LogLevel.Information,
        Message = "Delivery worker starting with {Concurrency} dispatchers and a batch size of {BatchSize}.")]
    public static partial void WorkerStarting(ILogger logger, int concurrency, int batchSize);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Delivery worker stopped.")]
    public static partial void WorkerStopped(ILogger logger);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Warning,
        Message = "Delivery worker is disabled by configuration; nothing will be dispatched.")]
    public static partial void WorkerDisabled(ILogger logger);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Debug, Message = "Claimed {Count} due deliveries.")]
    public static partial void Claimed(ILogger logger, int count);

    [LoggerMessage(
        EventId = 2004,
        Level = LogLevel.Information,
        Message = "Delivery {DeliveryId} attempt {AttemptNumber} returned {StatusCode} in {DurationMs}ms; now {Status}.")]
    public static partial void Attempted(
        ILogger logger,
        Guid deliveryId,
        int attemptNumber,
        int? statusCode,
        DeliveryStatus status,
        long durationMs);

    [LoggerMessage(
        EventId = 2005,
        Level = LogLevel.Debug,
        Message = "Delivery {DeliveryId} was {Status} rather than InFlight; another worker owns it.")]
    public static partial void SkippedNotInFlight(ILogger logger, Guid deliveryId, DeliveryStatus status);

    [LoggerMessage(
        EventId = 2006,
        Level = LogLevel.Warning,
        Message = "Delivery {DeliveryId} has no subscription or event left; dead-lettering it.")]
    public static partial void Orphaned(ILogger logger, Guid deliveryId);

    [LoggerMessage(EventId = 2007, Level = LogLevel.Error, Message = "Failed to claim due deliveries.")]
    public static partial void ClaimFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2008, Level = LogLevel.Error, Message = "Failed to process delivery {DeliveryId}.")]
    public static partial void ProcessFailed(ILogger logger, Guid deliveryId, Exception exception);

    [LoggerMessage(
        EventId = 2009,
        Level = LogLevel.Warning,
        Message = "Reclaimed {Count} deliveries whose worker did not finish them.")]
    public static partial void Reclaimed(ILogger logger, int count);

    [LoggerMessage(EventId = 2010, Level = LogLevel.Error, Message = "Failed to reclaim abandoned deliveries.")]
    public static partial void ReclaimFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 2011,
        Level = LogLevel.Information,
        Message = "Replayed delivery {SourceDeliveryId} as {NewDeliveryId} for event {EventId}.")]
    public static partial void Replayed(ILogger logger, Guid sourceDeliveryId, Guid newDeliveryId, Guid eventId);
}

/// <summary>Source-generated log messages for the realtime feed.</summary>
internal static partial class RealtimeLog
{
    [LoggerMessage(
        EventId = 3000,
        Level = LogLevel.Warning,
        Message = "Failed to broadcast the status change for delivery {DeliveryId}.")]
    public static partial void BroadcastFailed(ILogger logger, Guid deliveryId, Exception exception);
}
