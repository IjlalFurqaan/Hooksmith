using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hooksmith.Core;

/// <summary>
/// The JSON body Hooksmith POSTs to a subscriber.
/// </summary>
/// <param name="Id">The event id. Stable across every delivery and every replay of this event.</param>
/// <param name="Type">The event type.</param>
/// <param name="CreatedAt">When Hooksmith accepted the event from the producer.</param>
/// <param name="Data">The producer's payload, verbatim.</param>
/// <remarks>
/// <para>
/// The envelope is derived <em>only</em> from the event, never from the delivery or the attempt.
/// That is deliberate: a replay must produce byte-identical output to the original send, so the
/// same event id reaches the subscriber and a receiver deduplicating on <c>id</c> actually can.
/// Anything delivery-specific — the delivery id, the attempt number — travels in headers, where
/// it is visible for debugging without disturbing the signed bytes.
/// </para>
/// </remarks>
public sealed record DeliveryEnvelope(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("data")] JsonElement Data)
{
    /// <summary>
    /// Serialisation settings for the outbound body.
    /// </summary>
    /// <remarks>
    /// Pinned rather than inherited from the host's configuration. The bytes produced here are
    /// the bytes the HMAC is computed over, so a change to the API's global JSON options must
    /// not be able to silently invalidate every subscriber's signature verification.
    /// </remarks>
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    /// <summary>Builds the envelope for an event.</summary>
    /// <param name="event">The event being delivered.</param>
    /// <returns>The envelope.</returns>
    public static DeliveryEnvelope ForEvent(Event @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        return new DeliveryEnvelope(@event.Id, @event.Type, @event.ReceivedAt, @event.Payload.RootElement);
    }

    /// <summary>Renders the exact body to put on the wire and sign.</summary>
    /// <param name="event">The event being delivered.</param>
    /// <returns>The serialised envelope.</returns>
    public static string Serialise(Event @event) =>
        JsonSerializer.Serialize(ForEvent(@event), SerializerOptions);
}
