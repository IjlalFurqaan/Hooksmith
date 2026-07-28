using System.Text.Json;
using Hooksmith.Api.Contracts;
using Hooksmith.Api.Services;
using Hooksmith.Core;
using Microsoft.AspNetCore.Mvc;

namespace Hooksmith.Api.Controllers;

/// <summary>Event ingest.</summary>
[ApiController]
[Route("api/v1/events")]
public sealed class EventsController : ControllerBase
{
    /// <summary>The header producers use to make ingest idempotent.</summary>
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    private const int MaxIdempotencyKeyLength = 255;

    private readonly EventIngestService _ingest;

    /// <summary>Initialises the controller.</summary>
    /// <param name="ingest">The ingest service.</param>
    public EventsController(EventIngestService ingest)
    {
        ArgumentNullException.ThrowIfNull(ingest);
        _ingest = ingest;
    }

    /// <summary>Accepts an event and queues it for delivery.</summary>
    /// <param name="request">The event type and payload.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>202 with the event id and the number of deliveries created.</returns>
    /// <remarks>
    /// <para>
    /// Returns 202, not 200: the event has been durably recorded and fanned out, but nothing
    /// has been delivered yet. Claiming otherwise would be a lie the producer might act on.
    /// </para>
    /// <para>
    /// Supply an <c>Idempotency-Key</c> header to make retries safe. The same key within 24
    /// hours returns the original event id and creates no further deliveries, which is what
    /// lets a producer retry a timed-out ingest without double-sending to every subscriber.
    /// </para>
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(typeof(IngestEventResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IngestEventResponse>> Ingest(
        [FromBody] IngestEventRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!EventType.IsValid(request.Type))
        {
            ModelState.AddModelError(
                nameof(request.Type),
                $"Event type is required and must be at most {EventType.MaxLength} characters.");
            return ValidationProblem(ModelState);
        }

        if (request.Payload.ValueKind is JsonValueKind.Undefined)
        {
            ModelState.AddModelError(nameof(request.Payload), "Payload is required.");
            return ValidationProblem(ModelState);
        }

        if (!TryReadIdempotencyKey(out var idempotencyKey))
        {
            ModelState.AddModelError(
                IdempotencyKeyHeader,
                $"Idempotency-Key must be at most {MaxIdempotencyKeyLength} characters.");
            return ValidationProblem(ModelState);
        }

        // JsonElement is a window onto the request's buffer, which is recycled once the
        // response is written. Cloning into a standalone JsonDocument detaches the payload
        // from that lifetime before it reaches EF.
        using var payload = JsonDocument.Parse(request.Payload.GetRawText());

        var result = await _ingest
            .IngestAsync(request.Type, payload, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        return Accepted(new IngestEventResponse(result.EventId, result.DeliveryCount, result.IsDuplicate));
    }

    private bool TryReadIdempotencyKey(out string? idempotencyKey)
    {
        idempotencyKey = null;

        if (!Request.Headers.TryGetValue(IdempotencyKeyHeader, out var values))
        {
            return true;
        }

        var candidate = values.ToString().Trim();
        if (candidate.Length == 0)
        {
            return true;
        }

        if (candidate.Length > MaxIdempotencyKeyLength)
        {
            return false;
        }

        idempotencyKey = candidate;
        return true;
    }
}
