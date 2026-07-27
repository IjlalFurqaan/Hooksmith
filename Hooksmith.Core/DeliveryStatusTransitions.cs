using System.Collections.Frozen;

namespace Hooksmith.Core;

/// <summary>
/// The single authoritative definition of which <see cref="DeliveryStatus"/>
/// transitions are legal.
/// </summary>
/// <remarks>
/// <para>The table is expressed as data rather than as branching logic. That buys three
/// things: the whole state machine is readable in one screenful, a test can enumerate
/// the full cartesian product of statuses and assert on every edge, and adding a status
/// is a one-line change instead of an audit of every <c>if</c> in the codebase.</para>
/// <para>The permitted edges are:</para>
/// <code>
/// Pending      -> InFlight
/// InFlight     -> Succeeded | Retrying | DeadLettered
/// Retrying     -> InFlight
/// Succeeded    -> (terminal)
/// DeadLettered -> Pending   (replay only)
/// </code>
/// </remarks>
public static class DeliveryStatusTransitions
{
    private static readonly FrozenDictionary<DeliveryStatus, FrozenSet<DeliveryStatus>> Table =
        new Dictionary<DeliveryStatus, FrozenSet<DeliveryStatus>>
        {
            [DeliveryStatus.Pending] = FrozenSet.ToFrozenSet([DeliveryStatus.InFlight]),
            [DeliveryStatus.InFlight] = FrozenSet.ToFrozenSet(
            [
                DeliveryStatus.Succeeded,
                DeliveryStatus.Retrying,
                DeliveryStatus.DeadLettered,
            ]),
            [DeliveryStatus.Retrying] = FrozenSet.ToFrozenSet([DeliveryStatus.InFlight]),
            [DeliveryStatus.Succeeded] = FrozenSet<DeliveryStatus>.Empty,
            [DeliveryStatus.DeadLettered] = FrozenSet.ToFrozenSet([DeliveryStatus.Pending]),
        }.ToFrozenDictionary();

    /// <summary>Determines whether moving from <paramref name="from"/> to <paramref name="to"/> is legal.</summary>
    /// <param name="from">The current status.</param>
    /// <param name="to">The proposed next status.</param>
    /// <returns><see langword="true"/> if the transition appears in the table.</returns>
    public static bool IsLegal(DeliveryStatus from, DeliveryStatus to) =>
        Table.TryGetValue(from, out var allowed) && allowed.Contains(to);

    /// <summary>Lists every status reachable in one step from <paramref name="from"/>.</summary>
    /// <param name="from">The current status.</param>
    /// <returns>The permitted next statuses; empty for a terminal status.</returns>
    public static IReadOnlySet<DeliveryStatus> NextStatesFrom(DeliveryStatus from) =>
        Table.TryGetValue(from, out var allowed) ? allowed : FrozenSet<DeliveryStatus>.Empty;

    /// <summary>Determines whether a status has no outgoing transitions.</summary>
    /// <param name="status">The status to inspect.</param>
    /// <returns><see langword="true"/> if no transition leaves <paramref name="status"/>.</returns>
    public static bool IsTerminal(DeliveryStatus status) => NextStatesFrom(status).Count == 0;

    /// <summary>Throws unless the transition is legal.</summary>
    /// <param name="from">The current status.</param>
    /// <param name="to">The proposed next status.</param>
    /// <exception cref="InvalidDeliveryTransitionException">The transition is not in the table.</exception>
    public static void EnsureLegal(DeliveryStatus from, DeliveryStatus to)
    {
        if (!IsLegal(from, to))
        {
            throw new InvalidDeliveryTransitionException(from, to);
        }
    }
}
