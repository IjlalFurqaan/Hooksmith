using System.Text.Json;

namespace Hooksmith.Core;

/// <summary>
/// An event accepted from a producer. Immutable once ingested: the payload that
/// gets replayed a week later is byte-for-byte the payload that arrived.
/// </summary>
/// <param name="Id">
/// Stable identifier. This id travels with every delivery and every replay of the
/// event, which is what lets a well-behaved receiver deduplicate.
/// </param>
/// <param name="Type">The event type, matched against <see cref="Subscription.EventTypes"/>.</param>
/// <param name="Payload">The producer-supplied JSON body.</param>
/// <param name="ReceivedAt">When the event was accepted.</param>
/// <param name="IdempotencyKey">
/// Optional producer-supplied key from the <c>Idempotency-Key</c> header. A repeat of the
/// same key within the retention window returns the original event instead of fanning out again.
/// </param>
public sealed record Event(
    Guid Id,
    string Type,
    JsonDocument Payload,
    DateTimeOffset ReceivedAt,
    string? IdempotencyKey);
