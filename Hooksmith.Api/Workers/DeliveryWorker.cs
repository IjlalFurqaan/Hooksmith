using System.Threading.Channels;
using Hooksmith.Api.Logging;
using Hooksmith.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Hooksmith.Api.Workers;

/// <summary>
/// The hosted delivery loop: claims due deliveries and feeds them to a fixed pool of dispatchers.
/// </summary>
/// <remarks>
/// <para>
/// One producer reads due deliveries from Postgres; N consumers dispatch them. The channel
/// between them is <em>bounded</em>, which is the part that matters. An unbounded channel — or
/// worse, a <c>Task.Run</c> per due delivery — turns a backlog of fifty thousand deliveries
/// into fifty thousand simultaneous sockets and an out-of-memory crash. A bounded channel makes
/// the producer wait when consumers are saturated, so the claim rate self-regulates to the
/// dispatch rate.
/// </para>
/// <para>
/// This runs inside <c>Hooksmith.Api</c> rather than as a separate deployable. Running several
/// API instances runs several workers, and <c>SKIP LOCKED</c> is what keeps them from colliding.
/// </para>
/// </remarks>
public sealed class DeliveryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<DeliveryWorkerOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DeliveryWorker> _logger;

    /// <summary>Initialises the worker.</summary>
    /// <param name="scopeFactory">Creates a scope, and therefore a DbContext, per unit of work.</param>
    /// <param name="options">Worker configuration.</param>
    /// <param name="timeProvider">Clock used for polling delays.</param>
    /// <param name="logger">Log sink.</param>
    public DeliveryWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<DeliveryWorkerOptions> options,
        TimeProvider timeProvider,
        ILogger<DeliveryWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;

        if (!options.Enabled)
        {
            DeliveryLog.WorkerDisabled(_logger);
            return;
        }

        DeliveryLog.WorkerStarting(_logger, options.Concurrency, options.BatchSize);

        // Capacity equal to the consumer count: enough to keep every consumer fed, small enough
        // that the producer blocks rather than racing ahead claiming work nobody can start.
        var channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(options.Concurrency)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = false,
        });

        var consumers = Enumerable.Range(0, options.Concurrency)
            .Select(_ => ConsumeAsync(channel.Reader, stoppingToken))
            .ToArray();

        var producer = ProduceAsync(channel.Writer, options, stoppingToken);
        var reclaimer = ReclaimAsync(options, stoppingToken);

        await Task.WhenAll([producer, reclaimer, .. consumers]).ConfigureAwait(false);

        DeliveryLog.WorkerStopped(_logger);
    }

    private async Task ProduceAsync(
        ChannelWriter<Guid> writer,
        DeliveryWorkerOptions options,
        CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                IReadOnlyList<Guid> claimed;

                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var claims = scope.ServiceProvider.GetRequiredService<IDeliveryClaimStore>();
                    claimed = await claims
                        .ClaimDueAsync(options.BatchSize, options.ClaimLease, stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A database blip must not kill the loop. Backing off and retrying is the
                    // difference between a transient outage and a worker that silently stopped.
                    DeliveryLog.ClaimFailed(_logger, ex);
                    await DelayAsync(options.PollInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (claimed.Count == 0)
                {
                    await DelayAsync(options.PollInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                DeliveryLog.Claimed(_logger, claimed.Count);

                foreach (var deliveryId in claimed)
                {
                    await writer.WriteAsync(deliveryId, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ordinary shutdown.
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task ConsumeAsync(ChannelReader<Guid> reader, CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var deliveryId in reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var processor = scope.ServiceProvider.GetRequiredService<DeliveryProcessor>();
                    await processor.ProcessAsync(deliveryId, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The delivery stays in flight and the reclaim sweep will pick it up, so a
                    // poisoned delivery costs one attempt rather than the whole consumer.
                    DeliveryLog.ProcessFailed(_logger, deliveryId, ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ordinary shutdown.
        }
    }

    /// <summary>
    /// Periodically returns deliveries whose worker died mid-flight.
    /// </summary>
    /// <remarks>
    /// Without this, a process killed between claiming and recording leaves its deliveries in
    /// flight forever: invisible to the claim query, never retried, never dead-lettered. That is
    /// the price of counting the attempt at claim time, and this is how it is paid.
    /// </remarks>
    private async Task ReclaimAsync(DeliveryWorkerOptions options, CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await DelayAsync(options.ReclaimInterval, stoppingToken).ConfigureAwait(false);

                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var claims = scope.ServiceProvider.GetRequiredService<IDeliveryClaimStore>();
                    var reclaimed = await claims.ReclaimAbandonedAsync(stoppingToken).ConfigureAwait(false);

                    if (reclaimed > 0)
                    {
                        DeliveryLog.Reclaimed(_logger, reclaimed);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    DeliveryLog.ReclaimFailed(_logger, ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ordinary shutdown.
        }
    }

    private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, _timeProvider, cancellationToken);
}
