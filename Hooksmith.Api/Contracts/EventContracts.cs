using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace Hooksmith.Api.Contracts;

/// <summary>Request body for ingesting an event.</summary>
/// <param name="Type">The event type, matched against subscriptions. Normalised to lowercase.</param>
/// <param name="Payload">
/// The producer's JSON body, stored verbatim and delivered verbatim. Hooksmith does not
/// interpret it beyond requiring that it is valid JSON.
/// </param>
/// <remarks>
/// Validation attributes sit on the constructor parameters, not the generated properties:
/// MVC reads record validation metadata off the primary constructor.
/// </remarks>
public sealed record IngestEventRequest(
    [Required][MaxLength(200)] string Type,
    [Required] JsonElement Payload);

/// <summary>The 202 response to an accepted event.</summary>
/// <param name="EventId">
/// The event id. This travels with every delivery and every replay of this event, so a
/// receiver can deduplicate on it.
/// </param>
/// <param name="DeliveryCount">How many deliveries fan-out created.</param>
/// <param name="Duplicate">
/// True when an <c>Idempotency-Key</c> matched an earlier event, in which case this is that
/// earlier event's id and no new deliveries were created.
/// </param>
public sealed record IngestEventResponse(Guid EventId, int DeliveryCount, bool Duplicate);
