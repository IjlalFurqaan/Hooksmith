namespace Hooksmith.Core;

/// <summary>
/// The lifecycle state of a single <see cref="Delivery"/>.
/// </summary>
/// <remarks>
/// Legal movement between these values is defined once, as data, in
/// <see cref="DeliveryStatusTransitions"/>. Nothing else in the system is
/// permitted to decide what a valid transition is.
/// </remarks>
public enum DeliveryStatus
{
    /// <summary>Created and waiting to be claimed by the delivery worker.</summary>
    Pending = 0,

    /// <summary>Claimed by a worker and currently being dispatched.</summary>
    InFlight = 1,

    /// <summary>The subscriber accepted the delivery with a 2xx response. Terminal.</summary>
    Succeeded = 2,

    /// <summary>The attempt failed in a way worth retrying, and a later attempt is scheduled.</summary>
    Retrying = 3,

    /// <summary>
    /// Abandoned: either the attempt budget was exhausted or the subscriber returned a
    /// response that will not improve on retry. Terminal unless explicitly replayed.
    /// </summary>
    DeadLettered = 4,
}
