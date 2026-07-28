using System.Globalization;
using Hooksmith.Core;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Hooksmith.Infrastructure.Persistence;

/// <summary>
/// Hands due deliveries to exactly one worker at a time.
/// </summary>
public interface IDeliveryClaimStore
{
    /// <summary>
    /// Atomically claims up to <paramref name="batchSize"/> due deliveries, moving them to
    /// <see cref="DeliveryStatus.InFlight"/> and incrementing their attempt counts.
    /// </summary>
    /// <param name="batchSize">Maximum number of deliveries to claim.</param>
    /// <param name="reclaimAfter">How long before an unfinished claim is considered lost.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The ids of the deliveries this caller now owns.</returns>
    Task<IReadOnlyList<Guid>> ClaimDueAsync(
        int batchSize,
        TimeSpan reclaimAfter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns deliveries whose claiming worker never finished to
    /// <see cref="DeliveryStatus.Retrying"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many deliveries were recovered.</returns>
    Task<int> ReclaimAbandonedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The Postgres implementation of <see cref="IDeliveryClaimStore"/>, built on
/// <c>SELECT … FOR UPDATE SKIP LOCKED</c>.
/// </summary>
/// <remarks>
/// <para>
/// The claim is a single statement: a CTE selects due rows with <c>FOR UPDATE SKIP LOCKED</c>
/// and the enclosing <c>UPDATE</c> flips them to in-flight, returning their ids. Doing both in
/// one statement means there is no window in which a row is locked but not yet marked, so a
/// worker cannot crash between the two and leave a row locked-but-claimable.
/// </para>
/// <para>
/// <c>SKIP LOCKED</c> is what makes multiple worker instances safe. Without it, the second
/// worker's <c>SELECT … FOR UPDATE</c> blocks on the first worker's locks, waits for its
/// transaction, and then re-reads the same rows — serialising the whole fleet behind one
/// worker and, worse, handing it rows the first worker has already claimed. With it, the
/// second worker steps over locked rows and takes the next unlocked ones instead.
/// </para>
/// <para>
/// The HTTP request deliberately happens <em>outside</em> this transaction. Holding a row lock
/// across a ten-second call to someone else's server would tie up a database connection per
/// in-flight delivery and make a slow subscriber a database problem.
/// </para>
/// </remarks>
public sealed class DeliveryClaimStore : IDeliveryClaimStore
{
    /// <summary>
    /// The statuses a delivery can be claimed from, derived from the transition table rather
    /// than hard-coded.
    /// </summary>
    /// <remarks>
    /// This is the seam where SQL and the domain state machine could silently diverge. Deriving
    /// the list from <see cref="DeliveryStatusTransitions"/> means adding a status with a legal
    /// edge to <see cref="DeliveryStatus.InFlight"/> updates the query for free, and removing
    /// one cannot leave the query claiming rows the domain would refuse to transition.
    /// </remarks>
    public static readonly string[] ClaimableStatuses = Enum.GetValues<DeliveryStatus>()
        .Where(status => DeliveryStatusTransitions.IsLegal(status, DeliveryStatus.InFlight))
        .Select(status => status.ToString())
        .ToArray();

    private static readonly string InFlight = DeliveryStatus.InFlight.ToString();
    private static readonly string Retrying = DeliveryStatus.Retrying.ToString();

    private const string ClaimSql = """
        WITH due AS (
            SELECT id
            FROM deliveries
            WHERE status = ANY(@statuses) AND next_attempt_at <= @now
            ORDER BY next_attempt_at
            LIMIT @batch_size
            FOR UPDATE SKIP LOCKED
        )
        UPDATE deliveries AS d
        SET status = @in_flight,
            attempt_count = d.attempt_count + 1,
            next_attempt_at = @reclaim_at
        FROM due
        WHERE d.id = due.id
        RETURNING d.id AS "Value"
        """;

    private const string ReclaimSql = """
        UPDATE deliveries
        SET status = @retrying, next_attempt_at = @now
        WHERE status = @in_flight AND next_attempt_at <= @now
        """;

    private readonly HooksmithDbContext _db;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initialises the store.</summary>
    /// <param name="db">The database context supplying the connection.</param>
    /// <param name="timeProvider">Clock deciding what "due" means.</param>
    public DeliveryClaimStore(HooksmithDbContext db, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _db = db;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> ClaimDueAsync(
        int batchSize,
        TimeSpan reclaimAfter,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        var now = _timeProvider.GetUtcNow();

        return await _db.Database
            .SqlQueryRaw<Guid>(
                ClaimSql,
                Parameter("statuses", ClaimableStatuses, NpgsqlDbType.Array | NpgsqlDbType.Text),
                Parameter("now", now, NpgsqlDbType.TimestampTz),
                Parameter("batch_size", batchSize, NpgsqlDbType.Integer),
                Parameter("in_flight", InFlight, NpgsqlDbType.Varchar),
                Parameter("reclaim_at", now + reclaimAfter, NpgsqlDbType.TimestampTz))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> ReclaimAbandonedAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        return await _db.Database
            .ExecuteSqlRawAsync(
                ReclaimSql,
                [
                    Parameter("retrying", Retrying, NpgsqlDbType.Varchar),
                    Parameter("now", now, NpgsqlDbType.TimestampTz),
                    Parameter("in_flight", InFlight, NpgsqlDbType.Varchar),
                ],
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static NpgsqlParameter Parameter(string name, object value, NpgsqlDbType type) =>
        new(name, type) { Value = value };

    /// <summary>The claimable-status list, for diagnostics and tests.</summary>
    /// <returns>A comma-separated rendering of <see cref="ClaimableStatuses"/>.</returns>
    public static string DescribeClaimableStatuses() =>
        string.Join(", ", ClaimableStatuses.Select(s => s.ToString(CultureInfo.InvariantCulture)));
}
