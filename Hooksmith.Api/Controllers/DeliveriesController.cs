using Hooksmith.Api.Contracts;
using Hooksmith.Api.Logging;
using Hooksmith.Api.Realtime;
using Hooksmith.Core;
using Hooksmith.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hooksmith.Api.Controllers;

/// <summary>Querying and replaying deliveries.</summary>
[ApiController]
[Route("api/v1/deliveries")]
public sealed class DeliveriesController : ControllerBase
{
    /// <summary>The largest page a client may request.</summary>
    public const int MaxPageSize = 200;

    private const int DefaultPageSize = 50;

    private readonly HooksmithDbContext _db;
    private readonly IDeliveryStatusNotifier _notifier;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DeliveriesController> _logger;

    /// <summary>Initialises the controller.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="notifier">Broadcasts the new delivery to dashboard clients.</param>
    /// <param name="timeProvider">Clock for the replayed delivery's timestamps.</param>
    /// <param name="logger">Log sink.</param>
    public DeliveriesController(
        HooksmithDbContext db,
        IDeliveryStatusNotifier notifier,
        TimeProvider timeProvider,
        ILogger<DeliveriesController> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _db = db;
        _notifier = notifier;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Lists deliveries, newest first.</summary>
    /// <param name="status">Restrict to one lifecycle state.</param>
    /// <param name="subscriptionId">Restrict to one subscription.</param>
    /// <param name="from">Only deliveries created at or after this instant.</param>
    /// <param name="to">Only deliveries created strictly before this instant.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Items per page, capped at <see cref="MaxPageSize"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>One page of matching deliveries.</returns>
    /// <remarks>
    /// Paginated rather than open-ended because the delivery table is the one that grows without
    /// bound. An unbounded list endpoint over it is an outage waiting for its first busy week.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<DeliverySummaryResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<DeliverySummaryResponse>>> List(
        [FromQuery] DeliveryStatus? status,
        [FromQuery] Guid? subscriptionId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = _db.Deliveries.AsNoTracking();

        if (status is { } wanted)
        {
            query = query.Where(d => d.Status == wanted);
        }

        if (subscriptionId is { } subscription)
        {
            query = query.Where(d => d.SubscriptionId == subscription);
        }

        if (from is { } start)
        {
            query = query.Where(d => d.CreatedAt >= start);
        }

        if (to is { } end)
        {
            query = query.Where(d => d.CreatedAt < end);
        }

        var totalCount = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        var items = await Project(query)
            .OrderByDescending(d => d.CreatedAt)
            .ThenByDescending(d => d.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Ok(new PagedResult<DeliverySummaryResponse>(items, page, pageSize, totalCount));
    }

    /// <summary>Fetches one delivery with its attempt timeline.</summary>
    /// <param name="id">The delivery id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The delivery and every attempt made against it, or 404.</returns>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(DeliveryDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeliveryDetailResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var summary = await Project(_db.Deliveries.AsNoTracking().Where(d => d.Id == id))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (summary is null)
        {
            return NotFound();
        }

        var attempts = await _db.Attempts
            .AsNoTracking()
            .Where(a => a.DeliveryId == id)
            .OrderBy(a => a.AttemptNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var @event = await _db.Events
            .AsNoTracking()
            .SingleAsync(e => e.Id == summary.EventId, cancellationToken)
            .ConfigureAwait(false);

        return Ok(new DeliveryDetailResponse(
            summary,
            attempts.Select(AttemptResponse.From).ToList(),
            @event.Payload.RootElement.Clone(),
            CanReplay: IsReplayable(summary.Status)));
    }

    /// <summary>Replays a delivery.</summary>
    /// <param name="id">The delivery to replay.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>202 with the new delivery's id, 404, or 409 if the delivery is not replayable.</returns>
    /// <remarks>
    /// <para>
    /// Replay creates a <em>new</em> delivery referencing the same event rather than resetting
    /// the old one. The original attempt history is the record of what went wrong; overwriting
    /// it to try again would destroy the evidence at exactly the moment someone is investigating.
    /// </para>
    /// <para>
    /// The re-delivered payload carries the same event id, which is what lets a well-behaved
    /// receiver recognise it as something it may already have processed.
    /// </para>
    /// <para>
    /// Only <see cref="DeliveryStatus.DeadLettered"/> and <see cref="DeliveryStatus.Succeeded"/>
    /// may be replayed. Replaying something still <see cref="DeliveryStatus.Pending"/>,
    /// <see cref="DeliveryStatus.Retrying"/> or <see cref="DeliveryStatus.InFlight"/> would race
    /// the worker and double-send.
    /// </para>
    /// </remarks>
    [HttpPost("{id:guid}/replay")]
    [ProducesResponseType(typeof(ReplayResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ReplayResponse>> Replay(Guid id, CancellationToken cancellationToken)
    {
        var source = await _db.Deliveries
            .AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (source is null)
        {
            return NotFound();
        }

        if (!IsReplayable(source.Status))
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Delivery cannot be replayed",
                detail: $"A delivery may only be replayed from DeadLettered or Succeeded; this one is {source.Status}.");
        }

        var replay = Core.Delivery.Create(source.EventId, source.SubscriptionId, _timeProvider);

        _db.Deliveries.Add(replay);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        DeliveryLog.Replayed(_logger, source.Id, replay.Id, source.EventId);
        await _notifier.DeliveryChangedAsync(replay, attempt: null, cancellationToken).ConfigureAwait(false);

        return Accepted(new ReplayResponse(replay.Id, source.Id, source.EventId));
    }

    /// <summary>Whether a delivery in this state may be replayed.</summary>
    private static bool IsReplayable(DeliveryStatus status) =>
        status is DeliveryStatus.DeadLettered or DeliveryStatus.Succeeded;

    /// <summary>
    /// Joins the display fields the dashboard needs onto a delivery query.
    /// </summary>
    /// <remarks>
    /// Projected in SQL rather than loaded and mapped in memory, so listing a page of deliveries
    /// is one query rather than one plus two per row.
    /// </remarks>
    private IQueryable<DeliverySummaryResponse> Project(IQueryable<Core.Delivery> deliveries) =>
        from delivery in deliveries
        join subscription in _db.Subscriptions.AsNoTracking()
            on delivery.SubscriptionId equals subscription.Id
        join @event in _db.Events.AsNoTracking()
            on delivery.EventId equals @event.Id
        select new DeliverySummaryResponse(
            delivery.Id,
            delivery.EventId,
            @event.Type,
            delivery.SubscriptionId,
            subscription.Name,
            delivery.Status,
            delivery.AttemptCount,
            delivery.NextAttemptAt,
            delivery.CreatedAt);
}
