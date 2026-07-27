namespace Hooksmith.Core;

/// <summary>
/// Thrown when a <see cref="Delivery"/> is asked to move between two
/// <see cref="DeliveryStatus"/> values that <see cref="DeliveryStatusTransitions"/>
/// does not permit.
/// </summary>
/// <remarks>
/// This is deliberately an exception rather than a boolean result. An illegal
/// transition is a bug in the caller, not an expected runtime condition, and
/// silently swallowing it would let a delivery drift into a state the worker's
/// queries do not account for.
/// </remarks>
public sealed class InvalidDeliveryTransitionException : InvalidOperationException
{
    /// <summary>Initialises the exception for a specific rejected transition.</summary>
    /// <param name="from">The status the delivery was in.</param>
    /// <param name="to">The status that was illegally requested.</param>
    public InvalidDeliveryTransitionException(DeliveryStatus from, DeliveryStatus to)
        : base($"Delivery cannot transition from {from} to {to}.")
    {
        From = from;
        To = to;
    }

    /// <summary>The status the delivery was in when the transition was attempted.</summary>
    public DeliveryStatus From { get; }

    /// <summary>The status that was requested and rejected.</summary>
    public DeliveryStatus To { get; }
}
