namespace Hooksmith.Core.Retry;

/// <summary>
/// Decides whether a failed delivery gets another attempt, and when.
/// </summary>
/// <remarks>
/// <para>
/// The schedule is exponential backoff with <em>equal jitter</em> on a base of two seconds:
/// </para>
/// <code>
/// raw   = 2^(attempt-1) seconds
/// delay = raw/2 + random(0, raw/2)
/// </code>
/// <para>
/// The <c>raw/2</c> floor guarantees the backoff still grows — attempt 6 is never
/// scheduled sooner than 16 seconds out — while the random half spreads the load.
/// The jitter is the point of the exercise, not decoration. Without it, every delivery
/// that failed against a subscriber during an outage is scheduled from the same base
/// instant with the same formula, so the moment that subscriber comes back it is hit by
/// the entire backlog simultaneously and falls over again. Equal jitter smears the
/// backlog across the second half of each window.
/// </para>
/// </remarks>
public sealed class RetryPolicy
{
    /// <summary>Total attempts a delivery gets, including the first one.</summary>
    public const int MaxAttempts = 6;

    /// <summary>
    /// The exponential base. Attempt <c>n</c> has a raw window of <c>2^(n-1)</c> seconds, so
    /// attempt 2 is a 2-second window and attempt 6 is a 32-second one.
    /// </summary>
    public const double ExponentialBase = 2d;

    private readonly TimeProvider _timeProvider;
    private readonly Random _random;
    private readonly Lock _randomLock = new();

    /// <summary>Initialises the policy.</summary>
    /// <param name="timeProvider">Clock used to turn a delay into an absolute due time.</param>
    /// <param name="random">
    /// Jitter source. Defaults to <see cref="Random.Shared"/>; tests pass a seeded instance.
    /// </param>
    public RetryPolicy(TimeProvider timeProvider, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        _timeProvider = timeProvider;
        _random = random ?? Random.Shared;
    }

    /// <summary>
    /// Computes the jittered delay before the given attempt number.
    /// </summary>
    /// <param name="attemptNumber">The 1-based attempt being scheduled.</param>
    /// <returns>
    /// A delay in <c>[raw/2, raw)</c> where <c>raw = 2^(attemptNumber-1)</c> seconds.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="attemptNumber"/> is below 1 or above <see cref="MaxAttempts"/>.
    /// Asking for the delay before an attempt that will never happen is a caller bug;
    /// use <see cref="TryGetNextAttemptTime"/>, which reports exhaustion as a result.
    /// </exception>
    public TimeSpan DelayForAttempt(int attemptNumber)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptNumber, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(attemptNumber, MaxAttempts);

        var half = RawWindowFor(attemptNumber).TotalSeconds / 2d;
        double jitter;

        // Random instances other than Random.Shared are not thread-safe, and the worker
        // runs N consumers against one policy. The lock costs nothing next to an HTTP call.
        lock (_randomLock)
        {
            jitter = _random.NextDouble();
        }

        return TimeSpan.FromSeconds(half + (jitter * half));
    }

    /// <summary>
    /// The un-jittered window for an attempt: <c>2^(attemptNumber-1)</c> seconds.
    /// </summary>
    /// <param name="attemptNumber">The 1-based attempt being scheduled.</param>
    /// <returns>The raw window. The jittered delay always falls in <c>[window/2, window)</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="attemptNumber"/> is below 1 or above <see cref="MaxAttempts"/>.
    /// </exception>
    public static TimeSpan RawWindowFor(int attemptNumber)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptNumber, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(attemptNumber, MaxAttempts);

        return TimeSpan.FromSeconds(Math.Pow(ExponentialBase, attemptNumber - 1));
    }

    /// <summary>
    /// Determines whether a delivery has attempts left, and when the next one is due.
    /// </summary>
    /// <param name="attemptCount">Attempts already started for the delivery.</param>
    /// <param name="nextAttemptAt">When set, the absolute time the next attempt becomes due.</param>
    /// <returns>
    /// <see langword="false"/> when the attempt budget is exhausted and the delivery should
    /// be dead-lettered; otherwise <see langword="true"/>.
    /// </returns>
    public bool TryGetNextAttemptTime(int attemptCount, out DateTimeOffset nextAttemptAt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attemptCount);

        var nextAttemptNumber = attemptCount + 1;
        if (nextAttemptNumber > MaxAttempts)
        {
            nextAttemptAt = default;
            return false;
        }

        nextAttemptAt = _timeProvider.GetUtcNow() + DelayForAttempt(nextAttemptNumber);
        return true;
    }

    /// <summary>Whether a delivery with this many attempts has budget for another.</summary>
    /// <param name="attemptCount">Attempts already started for the delivery.</param>
    /// <returns><see langword="true"/> if another attempt is permitted.</returns>
    public static bool HasAttemptsRemaining(int attemptCount) => attemptCount < MaxAttempts;
}
