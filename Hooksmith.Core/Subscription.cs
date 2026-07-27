namespace Hooksmith.Core;

/// <summary>
/// A registered subscriber endpoint: where deliveries go, which event types it
/// wants, and the shared secret its payloads are signed with.
/// </summary>
/// <param name="Id">Stable identifier for the subscription.</param>
/// <param name="Name">Human-readable label, shown in the dashboard.</param>
/// <param name="Url">Absolute HTTP(S) endpoint that receives the POST.</param>
/// <param name="Secret">Shared secret used to compute the <c>X-Hooksmith-Signature</c> header.</param>
/// <param name="EventTypes">Event types this subscription matches. Empty means it matches nothing.</param>
/// <param name="IsActive">Whether fan-out considers this subscription. Inactive subscriptions receive nothing.</param>
/// <param name="CreatedAt">When the subscription was registered.</param>
public sealed record Subscription(
    Guid Id,
    string Name,
    Uri Url,
    string Secret,
    IReadOnlyList<string> EventTypes,
    bool IsActive,
    DateTimeOffset CreatedAt)
{
    /// <summary>
    /// Determines whether this subscription should receive an event of the given type.
    /// </summary>
    /// <param name="eventType">The event type being fanned out.</param>
    /// <returns>
    /// <see langword="true"/> when the subscription is active and lists
    /// <paramref name="eventType"/>.
    /// </returns>
    /// <remarks>
    /// Matching is case-insensitive. Treating <c>invoice.paid</c> and <c>Invoice.Paid</c>
    /// as different events is the kind of surprise that costs a subscriber a production
    /// incident, and there is no upside to the stricter rule.
    /// </remarks>
    public bool Matches(string eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);

        if (!IsActive)
        {
            return false;
        }

        for (var i = 0; i < EventTypes.Count; i++)
        {
            if (string.Equals(EventTypes[i], eventType, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
