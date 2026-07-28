using System.Text.Json;
using Hooksmith.Api.Logging;
using Hooksmith.Core;
using Hooksmith.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Hooksmith.Api.Services;

/// <summary>The outcome of ingesting one event.</summary>
/// <param name="EventId">The stored event's id.</param>
/// <param name="DeliveryCount">How many deliveries fan-out created.</param>
/// <param name="IsDuplicate">Whether an idempotency key matched an earlier event.</param>
public sealed record EventIngestResult(Guid EventId, int DeliveryCount, bool IsDuplicate);

/// <summary>
/// Accepts an event, stores it, and fans out one delivery per matching subscription.
/// </summary>
/// <remarks>
/// Ingest never dispatches. It writes the event and its delivery rows in a single
/// transaction and returns; the worker picks them up separately. That separation is what
/// keeps a producer's request latency independent of how slow, or how numerous, its
/// subscribers are.
/// </remarks>
public sealed class EventIngestService
{
    /// <summary>How long an idempotency key is honoured.</summary>
    public static readonly TimeSpan IdempotencyWindow = TimeSpan.FromHours(24);

    private const string IdempotencyKeyIndexName = "ux_events_idempotency_key";

    private readonly HooksmithDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EventIngestService> _logger;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="timeProvider">Clock used for receipt and delivery timestamps.</param>
    /// <param name="logger">Log sink.</param>
    public EventIngestService(HooksmithDbContext db, TimeProvider timeProvider, ILogger<EventIngestService> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _db = db;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Ingests an event and fans it out.</summary>
    /// <param name="type">The event type, normalised internally.</param>
    /// <param name="payload">The producer's JSON body.</param>
    /// <param name="idempotencyKey">Optional key from the <c>Idempotency-Key</c> header.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The stored event's id and how many deliveries were created.</returns>
    public async Task<EventIngestResult> IngestAsync(
        string type,
        JsonDocument payload,
        string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(payload);

        var normalisedType = EventType.Normalise(type);
        var now = _timeProvider.GetUtcNow();

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var existing = await FindReplayableEventAsync(idempotencyKey, now, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                var deliveryCount = await _db.Deliveries
                    .CountAsync(d => d.EventId == existing.Id, cancellationToken)
                    .ConfigureAwait(false);

                IngestLog.IdempotentReplay(_logger, idempotencyKey, existing.Id);

                return new EventIngestResult(existing.Id, deliveryCount, IsDuplicate: true);
            }
        }

        try
        {
            return await StoreAndFanOutAsync(normalisedType, payload, idempotencyKey, now, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsIdempotencyKeyConflict(ex))
        {
            // Two requests carrying the same key raced past the read above. The unique index
            // is the actual arbiter: exactly one insert survives, and this one did not. Fall
            // back to reporting the winner's event rather than failing the producer.
            _db.ChangeTracker.Clear();

            var winner = await _db.Events
                .AsNoTracking()
                .SingleAsync(e => e.IdempotencyKey == idempotencyKey, cancellationToken)
                .ConfigureAwait(false);

            var deliveryCount = await _db.Deliveries
                .CountAsync(d => d.EventId == winner.Id, cancellationToken)
                .ConfigureAwait(false);

            IngestLog.IdempotencyRaceLost(_logger, idempotencyKey, winner.Id);

            return new EventIngestResult(winner.Id, deliveryCount, IsDuplicate: true);
        }
    }

    /// <summary>
    /// Finds an event whose idempotency key is still inside the honoured window.
    /// </summary>
    /// <remarks>
    /// A key found <em>outside</em> the window is released rather than reused: its event keeps
    /// existing but gives up the key, so the incoming request can claim it and be treated as
    /// genuinely new. Without that step the unique index would keep the key reserved forever
    /// and the 24-hour window would silently become permanent.
    /// </remarks>
    private async Task<Event?> FindReplayableEventAsync(
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await _db.Events
            .SingleOrDefaultAsync(e => e.IdempotencyKey == idempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            return null;
        }

        if (now - existing.ReceivedAt <= IdempotencyWindow)
        {
            return existing;
        }

        _db.Entry(existing).CurrentValues[nameof(Event.IdempotencyKey)] = null;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _db.ChangeTracker.Clear();

        IngestLog.IdempotencyKeyReleased(_logger, idempotencyKey, existing.Id);

        return null;
    }

    private async Task<EventIngestResult> StoreAndFanOutAsync(
        string normalisedType,
        JsonDocument payload,
        string? idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var @event = new Event(
            Id: Guid.NewGuid(),
            Type: normalisedType,
            Payload: payload,
            ReceivedAt: now,
            IdempotencyKey: string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey);

        // Postgres array containment, which the GIN index on event_types serves directly.
        var matchingSubscriptionIds = await _db.Subscriptions
            .Where(s => s.IsActive && s.EventTypes.Contains(normalisedType))
            .Select(s => s.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var deliveries = matchingSubscriptionIds
            .Select(subscriptionId => Delivery.Create(@event.Id, subscriptionId, _timeProvider))
            .ToList();

        _db.Events.Add(@event);
        _db.Deliveries.AddRange(deliveries);

        // One SaveChanges, therefore one transaction. An event that exists without its
        // deliveries would be an event that silently never gets sent to anyone.
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        IngestLog.Ingested(_logger, @event.Id, normalisedType, deliveries.Count);

        return new EventIngestResult(@event.Id, deliveries.Count, IsDuplicate: false);
    }

    private static bool IsIdempotencyKeyConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
        && string.Equals(postgres.ConstraintName, IdempotencyKeyIndexName, StringComparison.Ordinal);
}
