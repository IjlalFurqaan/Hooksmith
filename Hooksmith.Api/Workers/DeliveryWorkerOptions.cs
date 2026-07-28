namespace Hooksmith.Api.Workers;

/// <summary>Configuration for the delivery worker.</summary>
public sealed class DeliveryWorkerOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "DeliveryWorker";

    /// <summary>Whether the worker runs at all. Turning it off leaves ingest working.</summary>
    /// <remarks>
    /// Integration tests drive dispatch deterministically rather than racing a timer, so they
    /// disable the hosted loop and pump it by hand.
    /// </remarks>
    public bool Enabled { get; init; } = true;

    /// <summary>How many deliveries are dispatched concurrently.</summary>
    /// <remarks>
    /// This is the whole point of the channel: a fixed number of consumers, not one task per
    /// due delivery. A backlog of fifty thousand deliveries must not become fifty thousand
    /// simultaneous sockets.
    /// </remarks>
    public int Concurrency { get; init; } = 8;

    /// <summary>How many deliveries one claim query takes at a time.</summary>
    public int BatchSize { get; init; } = 32;

    /// <summary>How long the producer waits after finding nothing due.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a claim is honoured before another worker may assume the claimer died.
    /// </summary>
    /// <remarks>
    /// Must comfortably exceed the dispatch timeout, or a merely slow subscriber will get the
    /// same delivery sent twice. Ten seconds of timeout against a sixty second lease leaves
    /// room for the response to be read and the outcome recorded.
    /// </remarks>
    public TimeSpan ClaimLease { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How often abandoned in-flight deliveries are swept back into the queue.</summary>
    public TimeSpan ReclaimInterval { get; init; } = TimeSpan.FromSeconds(30);
}
