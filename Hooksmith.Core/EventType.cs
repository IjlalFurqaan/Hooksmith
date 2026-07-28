namespace Hooksmith.Core;

/// <summary>
/// Normalisation rules for event type strings.
/// </summary>
/// <remarks>
/// Applied identically to <see cref="Subscription.EventTypes"/> on registration and to
/// <see cref="Event.Type"/> on ingest. Normalising on the way in is what lets fan-out be a
/// plain indexed array lookup in Postgres instead of a case-insensitive scan, and it means
/// a producer that sends <c>Invoice.Paid</c> reaches a subscriber that registered
/// <c>invoice.paid</c>.
/// </remarks>
public static class EventType
{
    /// <summary>Maximum accepted length of an event type.</summary>
    public const int MaxLength = 200;

    /// <summary>Trims surrounding whitespace and lowercases an event type.</summary>
    /// <param name="eventType">The producer- or subscriber-supplied value.</param>
    /// <returns>The normalised value.</returns>
    /// <exception cref="ArgumentException"><paramref name="eventType"/> is null, empty or whitespace.</exception>
    public static string Normalise(string eventType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        return eventType.Trim().ToLowerInvariant();
    }

    /// <summary>Normalises a set of event types, discarding blanks and duplicates.</summary>
    /// <param name="eventTypes">The values to normalise.</param>
    /// <returns>The distinct normalised values, in first-seen order.</returns>
    public static IReadOnlyList<string> NormaliseAll(IEnumerable<string> eventTypes)
    {
        ArgumentNullException.ThrowIfNull(eventTypes);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        foreach (var eventType in eventTypes)
        {
            if (string.IsNullOrWhiteSpace(eventType))
            {
                continue;
            }

            var normalised = Normalise(eventType);
            if (seen.Add(normalised))
            {
                result.Add(normalised);
            }
        }

        return result;
    }

    /// <summary>Whether a value is an acceptable event type.</summary>
    /// <param name="eventType">The value to check.</param>
    /// <returns><see langword="true"/> if it is non-blank and within <see cref="MaxLength"/>.</returns>
    public static bool IsValid(string? eventType) =>
        !string.IsNullOrWhiteSpace(eventType) && eventType.Trim().Length <= MaxLength;
}
