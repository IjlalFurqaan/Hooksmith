using Hooksmith.Api.Contracts;
using Hooksmith.Core;
using Hooksmith.Core.Signing;
using Hooksmith.Infrastructure.Persistence;
using Hooksmith.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hooksmith.Api.Controllers;

/// <summary>Registration and management of subscriber endpoints.</summary>
[ApiController]
[Route("api/v1/subscriptions")]
public sealed class SubscriptionsController : ControllerBase
{
    private readonly HooksmithDbContext _db;
    private readonly IOutboundUrlValidator _urlValidator;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initialises the controller.</summary>
    /// <param name="db">The database context.</param>
    /// <param name="urlValidator">SSRF guard applied to subscriber URLs.</param>
    /// <param name="timeProvider">Clock used for creation timestamps.</param>
    public SubscriptionsController(
        HooksmithDbContext db,
        IOutboundUrlValidator urlValidator,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(urlValidator);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _db = db;
        _urlValidator = urlValidator;
        _timeProvider = timeProvider;
    }

    /// <summary>Lists every registered subscription, newest first.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>All subscriptions, with secrets masked.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<SubscriptionResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SubscriptionResponse>>> List(
        CancellationToken cancellationToken)
    {
        var subscriptions = await _db.Subscriptions
            .AsNoTracking()
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Ok(subscriptions.Select(SubscriptionResponse.From).ToList());
    }

    /// <summary>Fetches one subscription.</summary>
    /// <param name="id">The subscription id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The subscription, or 404.</returns>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(SubscriptionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SubscriptionResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var subscription = await _db.Subscriptions
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return subscription is null ? NotFound() : Ok(SubscriptionResponse.From(subscription));
    }

    /// <summary>Registers a subscription.</summary>
    /// <param name="request">The subscription to create.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>201 with the created subscription and its secret, or 400 if the URL is rejected.</returns>
    /// <remarks>
    /// The URL is put through the SSRF guard here so a hostile endpoint is refused at the door
    /// rather than discovered by the delivery worker later.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(typeof(CreateSubscriptionResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CreateSubscriptionResponse>> Create(
        [FromBody] CreateSubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var url = await ValidateUrlAsync(request.Url, cancellationToken).ConfigureAwait(false);
        if (url is null)
        {
            return ValidationProblem(ModelState);
        }

        var eventTypes = EventType.NormaliseAll(request.EventTypes);
        if (eventTypes.Count == 0)
        {
            ModelState.AddModelError(
                nameof(request.EventTypes),
                "At least one non-blank event type is required.");
            return ValidationProblem(ModelState);
        }

        var subscription = new Subscription(
            Id: Guid.NewGuid(),
            Name: request.Name.Trim(),
            Url: url,
            Secret: string.IsNullOrWhiteSpace(request.Secret) ? WebhookSecret.Generate() : request.Secret,
            EventTypes: eventTypes,
            IsActive: request.IsActive,
            CreatedAt: _timeProvider.GetUtcNow());

        _db.Subscriptions.Add(subscription);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var response = new CreateSubscriptionResponse(
            SubscriptionResponse.From(subscription),
            subscription.Secret);

        return CreatedAtAction(nameof(Get), new { id = subscription.Id }, response);
    }

    /// <summary>Updates a subscription in place.</summary>
    /// <param name="id">The subscription id.</param>
    /// <param name="request">The fields to change; omitted fields are left alone.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The updated subscription, 404, or 400 if a new URL is rejected.</returns>
    [HttpPatch("{id:guid}")]
    [ProducesResponseType(typeof(SubscriptionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SubscriptionResponse>> Update(
        Guid id,
        [FromBody] UpdateSubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var subscription = await _db.Subscriptions
            .SingleOrDefaultAsync(s => s.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (subscription is null)
        {
            return NotFound();
        }

        var url = subscription.Url;
        if (request.Url is not null)
        {
            // Re-validated on every change. A subscription that was safe at registration is
            // not automatically safe after someone repoints it.
            var validated = await ValidateUrlAsync(request.Url, cancellationToken).ConfigureAwait(false);
            if (validated is null)
            {
                return ValidationProblem(ModelState);
            }

            url = validated;
        }

        var eventTypes = subscription.EventTypes;
        if (request.EventTypes is not null)
        {
            eventTypes = EventType.NormaliseAll(request.EventTypes);
            if (eventTypes.Count == 0)
            {
                ModelState.AddModelError(
                    nameof(request.EventTypes),
                    "At least one non-blank event type is required.");
                return ValidationProblem(ModelState);
            }
        }

        var updated = subscription with
        {
            Name = request.Name?.Trim() ?? subscription.Name,
            Url = url,
            EventTypes = eventTypes,
            IsActive = request.IsActive ?? subscription.IsActive,
            Secret = string.IsNullOrWhiteSpace(request.Secret) ? subscription.Secret : request.Secret,
        };

        _db.Entry(subscription).CurrentValues.SetValues(updated);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Ok(SubscriptionResponse.From(updated));
    }

    /// <summary>Deletes a subscription and everything recorded about it.</summary>
    /// <param name="id">The subscription id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>204, or 404 if it does not exist.</returns>
    /// <remarks>
    /// This cascades: the subscription's deliveries and their attempt history go too. To stop
    /// delivering while keeping the audit trail, PATCH <c>IsActive</c> to false instead.
    /// </remarks>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var deleted = await _db.Subscriptions
            .Where(s => s.Id == id)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        return deleted == 0 ? NotFound() : NoContent();
    }

    /// <summary>
    /// Parses and SSRF-validates a subscriber URL, recording the reason in
    /// <see cref="ControllerBase.ModelState"/> on failure.
    /// </summary>
    private async Task<Uri?> ValidateUrlAsync(string candidate, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var url))
        {
            ModelState.AddModelError("url", "Subscriber URL must be an absolute URI.");
            return null;
        }

        var result = await _urlValidator.ValidateAsync(url, cancellationToken).ConfigureAwait(false);
        if (result.IsAllowed)
        {
            return url;
        }

        ModelState.AddModelError("url", result.Detail ?? "Subscriber URL was rejected.");
        return null;
    }
}
