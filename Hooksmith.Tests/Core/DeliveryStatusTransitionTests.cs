using Hooksmith.Core;
using Microsoft.Extensions.Time.Testing;

namespace Hooksmith.Tests.Core;

/// <summary>
/// Exercises every edge of the transition table — both the five legal ones and all
/// twenty illegal ones — by enumerating the full cartesian product of statuses rather
/// than by hand-picking cases.
/// </summary>
public sealed class DeliveryStatusTransitionTests
{
    private static readonly DeliveryStatus[] AllStatuses = Enum.GetValues<DeliveryStatus>();

    /// <summary>The complete set of legal edges, restated independently of the implementation.</summary>
    private static readonly HashSet<(DeliveryStatus From, DeliveryStatus To)> ExpectedLegalEdges =
    [
        (DeliveryStatus.Pending, DeliveryStatus.InFlight),
        (DeliveryStatus.InFlight, DeliveryStatus.Succeeded),
        (DeliveryStatus.InFlight, DeliveryStatus.Retrying),
        (DeliveryStatus.InFlight, DeliveryStatus.DeadLettered),
        (DeliveryStatus.Retrying, DeliveryStatus.InFlight),
        (DeliveryStatus.DeadLettered, DeliveryStatus.Pending),
    ];

    public static TheoryData<DeliveryStatus, DeliveryStatus> AllEdges()
    {
        var data = new TheoryData<DeliveryStatus, DeliveryStatus>();
        foreach (var from in AllStatuses)
        {
            foreach (var to in AllStatuses)
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllEdges))]
    public void IsLegal_agrees_with_the_specified_table_on_every_edge(DeliveryStatus from, DeliveryStatus to)
    {
        var expected = ExpectedLegalEdges.Contains((from, to));

        DeliveryStatusTransitions.IsLegal(from, to).Should().Be(
            expected,
            "the table permits exactly the edges in the spec ({0} -> {1})",
            from,
            to);
    }

    [Theory]
    [MemberData(nameof(AllEdges))]
    public void TransitionTo_throws_on_exactly_the_illegal_edges(DeliveryStatus from, DeliveryStatus to)
    {
        var delivery = DeliveryAt(from);

        var transition = () => delivery.TransitionTo(to);

        if (ExpectedLegalEdges.Contains((from, to)))
        {
            transition.Should().NotThrow();
            transition().Status.Should().Be(to);
        }
        else
        {
            transition.Should().Throw<InvalidDeliveryTransitionException>()
                .Which.Should().Match<InvalidDeliveryTransitionException>(e => e.From == from && e.To == to);
        }
    }

    [Theory]
    [InlineData(DeliveryStatus.Succeeded)]
    public void Terminal_statuses_have_no_outgoing_edges(DeliveryStatus status)
    {
        DeliveryStatusTransitions.IsTerminal(status).Should().BeTrue();
        DeliveryStatusTransitions.NextStatesFrom(status).Should().BeEmpty();
    }

    [Theory]
    [InlineData(DeliveryStatus.Pending)]
    [InlineData(DeliveryStatus.InFlight)]
    [InlineData(DeliveryStatus.Retrying)]
    [InlineData(DeliveryStatus.DeadLettered)]
    public void Non_terminal_statuses_have_outgoing_edges(DeliveryStatus status)
    {
        DeliveryStatusTransitions.IsTerminal(status).Should().BeFalse();
        DeliveryStatusTransitions.NextStatesFrom(status).Should().NotBeEmpty();
    }

    [Fact]
    public void Create_starts_pending_and_due_immediately()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero));

        var delivery = Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), time);

        delivery.Status.Should().Be(DeliveryStatus.Pending);
        delivery.AttemptCount.Should().Be(0);
        delivery.NextAttemptAt.Should().Be(time.GetUtcNow());
        delivery.CreatedAt.Should().Be(time.GetUtcNow());
    }

    [Fact]
    public void Claim_increments_the_attempt_count_and_sets_a_reclaim_deadline()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero));
        var delivery = Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), time);

        var claimed = delivery.Claim(Lease(time));

        claimed.Status.Should().Be(DeliveryStatus.InFlight);
        claimed.AttemptCount.Should().Be(1);
        claimed.NextAttemptAt.Should().Be(
            Lease(time),
            "counting the attempt before the HTTP call is only safe if a delivery whose worker "
            + "died is eventually taken back");
    }

    [Fact]
    public void An_in_flight_delivery_is_abandoned_once_its_lease_expires()
    {
        var start = new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(start);
        var inFlight = Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), time).Claim(start.AddMinutes(1));

        inFlight.IsAbandonedAt(start.AddSeconds(59)).Should().BeFalse();
        inFlight.IsAbandonedAt(start.AddMinutes(1)).Should().BeTrue();

        var recovered = inFlight.ReclaimAfterWorkerLoss(time);
        recovered.Status.Should().Be(DeliveryStatus.Retrying);
        recovered.NextAttemptAt.Should().Be(start);
        recovered.AttemptCount.Should().Be(
            1,
            "the spent attempt is not refunded; the subscriber may already have processed it");
    }

    [Theory]
    [InlineData(DeliveryStatus.Pending)]
    [InlineData(DeliveryStatus.Retrying)]
    [InlineData(DeliveryStatus.Succeeded)]
    [InlineData(DeliveryStatus.DeadLettered)]
    public void Only_in_flight_deliveries_can_be_abandoned(DeliveryStatus status)
    {
        var delivery = DeliveryAt(status) with { NextAttemptAt = DateTimeOffset.UnixEpoch };

        delivery.IsAbandonedAt(DateTimeOffset.UtcNow.AddYears(1)).Should().BeFalse();
    }

    [Fact]
    public void A_retry_cycle_keeps_accumulating_attempts()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero));
        var delivery = Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), time);

        var afterTwoFailures = delivery
            .Claim(Lease(time))
            .ScheduleRetry(time.GetUtcNow().AddSeconds(1))
            .Claim(Lease(time))
            .ScheduleRetry(time.GetUtcNow().AddSeconds(3));

        afterTwoFailures.Status.Should().Be(DeliveryStatus.Retrying);
        afterTwoFailures.AttemptCount.Should().Be(2);
        afterTwoFailures.NextAttemptAt.Should().Be(time.GetUtcNow().AddSeconds(3));
    }

    [Fact]
    public void Succeeded_is_terminal_and_cannot_be_reopened()
    {
        var time = new FakeTimeProvider();
        var succeeded = Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), time).Claim(Lease(time)).Succeed();

        foreach (var status in AllStatuses)
        {
            var reopen = () => succeeded.TransitionTo(status);
            reopen.Should().Throw<InvalidDeliveryTransitionException>();
        }
    }

    [Fact]
    public void RestoreForReplay_requeues_a_dead_lettered_delivery_due_now()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero));
        var deadLettered = Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), time).Claim(Lease(time)).DeadLetter();

        time.Advance(TimeSpan.FromHours(6));
        var replayed = deadLettered.RestoreForReplay(time);

        replayed.Status.Should().Be(DeliveryStatus.Pending);
        replayed.NextAttemptAt.Should().Be(time.GetUtcNow());
        replayed.AttemptCount.Should().Be(1, "requeuing in place does not erase the attempts already spent");
    }

    [Fact]
    public void IsDueAt_only_returns_true_for_a_scheduled_delivery_whose_time_has_come()
    {
        var start = new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(start);
        var pending = Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), time);

        pending.IsDueAt(start).Should().BeTrue();
        pending.IsDueAt(start.AddSeconds(-1)).Should().BeFalse();

        var inFlight = pending.Claim(Lease(time));
        inFlight.IsDueAt(start.AddHours(1)).Should().BeFalse("an in-flight delivery is not claimable");

        var retrying = inFlight.ScheduleRetry(start.AddSeconds(30));
        retrying.IsDueAt(start.AddSeconds(29)).Should().BeFalse();
        retrying.IsDueAt(start.AddSeconds(30)).Should().BeTrue();

        var succeeded = pending.Claim(Lease(time)).Succeed();
        succeeded.IsDueAt(start.AddYears(1)).Should().BeFalse();
    }

    /// <summary>A reclaim deadline comfortably beyond any dispatch, for tests that do not care about it.</summary>
    private static DateTimeOffset Lease(TimeProvider time) => time.GetUtcNow().AddMinutes(1);

    /// <summary>Builds a delivery sitting in an arbitrary status, bypassing the state machine.</summary>
    private static Delivery DeliveryAt(DeliveryStatus status) =>
        new(
            Id: Guid.NewGuid(),
            EventId: Guid.NewGuid(),
            SubscriptionId: Guid.NewGuid(),
            Status: status,
            AttemptCount: 0,
            NextAttemptAt: null,
            CreatedAt: DateTimeOffset.UnixEpoch);
}
