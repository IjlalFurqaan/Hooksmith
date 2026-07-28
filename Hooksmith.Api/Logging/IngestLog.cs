namespace Hooksmith.Api.Logging;

/// <summary>
/// Source-generated log messages for event ingest.
/// </summary>
/// <remarks>
/// The <c>[LoggerMessage]</c> generator emits a strongly typed delegate per message, so the
/// arguments are never boxed into an <c>object[]</c> and the message is not formatted at all
/// when the level is disabled. On the ingest path — one call per accepted event — that is
/// worth the small amount of ceremony.
/// </remarks>
internal static partial class IngestLog
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "Ingested event {EventId} of type {EventType}, fanned out to {DeliveryCount} subscriptions.")]
    public static partial void Ingested(ILogger logger, Guid eventId, string eventType, int deliveryCount);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "Idempotency key {IdempotencyKey} matched event {EventId}; returning it unchanged.")]
    public static partial void IdempotentReplay(ILogger logger, string idempotencyKey, Guid eventId);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "Idempotency key {IdempotencyKey} lost an insert race; returning event {EventId} instead.")]
    public static partial void IdempotencyRaceLost(ILogger logger, string? idempotencyKey, Guid eventId);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Information,
        Message = "Idempotency key {IdempotencyKey} on event {EventId} is older than the window and has been released.")]
    public static partial void IdempotencyKeyReleased(ILogger logger, string idempotencyKey, Guid eventId);
}
