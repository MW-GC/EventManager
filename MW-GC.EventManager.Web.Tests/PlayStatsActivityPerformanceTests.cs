using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Tests;

// Issue #136: the Activity performance numbers of the play stats (win rate, average Likes and average attendees),
// through PlayStats.Compute only. "Now" is a fixed clock (PlayStatsFixture).
[TestClass]
public sealed class PlayStatsActivityPerformanceTests : PlayStatsFixture
{
    [TestMethod]
    public void WinRate_IsWinsDividedByOffered()
    {
        var report = Compute(
            Event(Day(3, 1), sprint.Id, Pick(kart, sprint), Pick(puzzle, tiles)),
            Event(Day(4, 1), tiles.Id, Pick(kart, sprint), Pick(puzzle, tiles)),
            Event(Day(5, 1), null, Pick(kart, sprint)),
            Event(Day(6, 1), tiles.Id, Pick(kart, sprint), Pick(puzzle, tiles)));

        Assert.AreEqual(0.25m, Activity(report, sprint.Id).Performance.WinRate);
        Assert.AreEqual(2m / 3m, Activity(report, tiles.Id).Performance.WinRate);
    }

    [TestMethod]
    public void AverageLikes_IsTheMeanLikeCount_OfTheEventsOfferedIn()
    {
        var first = Event(Day(3, 1), sprint.Id, Pick(kart, sprint), Pick(puzzle, tiles));
        var second = Event(Day(4, 1), null, Pick(kart, sprint));

        var report = Compute([Metrics(first, null, 3), Metrics(second, null, 2)], first, second);

        Assert.AreEqual(2.5m, Activity(report, sprint.Id).Performance.AverageLikes);
        Assert.AreEqual(3m, Activity(report, tiles.Id).Performance.AverageLikes);
    }

    [TestMethod]
    public void AverageAttendees_IsTheMeanAttendees_OfTheEventsOfferedIn()
    {
        var first = Event(Day(3, 1), sprint.Id, Pick(kart, sprint), Pick(puzzle, tiles));
        var second = Event(Day(4, 1), null, Pick(kart, sprint));

        var report = Compute([Metrics(first, 10, 0), Metrics(second, 15, 0)], first, second);

        Assert.AreEqual(12.5m, Activity(report, sprint.Id).Performance.AverageAttendees);
        Assert.AreEqual(10m, Activity(report, tiles.Id).Performance.AverageAttendees);
    }

    [TestMethod]
    public void AnEventWithNoMetricsItem_IsLeftOutOfAttendees_ButCountsZeroLikes()
    {
        var withMetrics = Event(Day(3, 1), sprint.Id, Pick(kart, sprint));
        var without = Event(Day(4, 1), null, Pick(kart, sprint));

        var report = Compute([Metrics(withMetrics, 10, 4)], withMetrics, without);

        Assert.AreEqual(10m, Activity(report, sprint.Id).Performance.AverageAttendees, "not (10 + 0) / 2");
        Assert.AreEqual(2m, Activity(report, sprint.Id).Performance.AverageLikes, "(4 + 0) / 2");
    }

    [TestMethod]
    public void ANullAttendees_IsLeftOutOfTheAttendeeAverage()
    {
        var recorded = Event(Day(3, 1), sprint.Id, Pick(kart, sprint));
        var cleared = Event(Day(4, 1), null, Pick(kart, sprint));

        var report = Compute([Metrics(recorded, 10, 0), Metrics(cleared, null, 2)], recorded, cleared);

        Assert.AreEqual(10m, Activity(report, sprint.Id).Performance.AverageAttendees);
        Assert.AreEqual(1m, Activity(report, sprint.Id).Performance.AverageLikes, "the cleared Event's Likes still count");
    }

    [TestMethod]
    public void AnActivityNeverOffered_HasNoWinRateNoAverageLikesAndNoAverageAttendees()
    {
        var ev = Event(Day(3, 1), sprint.Id, Pick(kart, sprint));

        var report = Compute([Metrics(ev, 10, 2)], ev);

        Assert.AreEqual(new ActivityPerformance(null, null, null), Activity(report, battle.Id).Performance);
        Assert.AreEqual(new ActivityPerformance(1m, 2m, 10m), Activity(report, sprint.Id).Performance);
    }

    [TestMethod]
    public void AnActivityOfferedButNeverWon_HasAZeroWinRate_NotNull()
    {
        var report = Compute(
            Event(Day(3, 1), sprint.Id, Pick(kart, sprint), Pick(puzzle, tiles)),
            Event(Day(4, 1), null, Pick(puzzle, tiles)));

        Assert.AreEqual(0m, Activity(report, tiles.Id).Performance.WinRate);
    }

    [TestMethod]
    public void ADeletedActivity_GetsThePerformanceNumbersToo()
    {
        var goneActivity = Guid.NewGuid();
        var first = Event(Day(3, 1), goneActivity, Pick(kart.Id, kart.Name, goneActivity, "Drift"));
        var second = Event(Day(4, 1), sprint.Id, Pick(kart.Id, kart.Name, goneActivity, "Drift"), Pick(kart, sprint));

        var report = Compute([Metrics(first, 8, 1), Metrics(second, 12, 4)], first, second);

        var deleted = Activity(report, goneActivity);
        Assert.IsTrue(deleted.Deleted);
        Assert.AreEqual(new ActivityPerformance(0.5m, 2.5m, 10m), deleted.Performance);
    }

    [TestMethod]
    public void AFutureEvent_IsNotCountedInThePerformanceNumbers()
    {
        var past = Event(Day(3, 1), sprint.Id, Pick(kart, sprint));
        var future = Event(Day(12, 24), null, Pick(kart, sprint));

        var report = Compute([Metrics(past, 10, 2), Metrics(future, 100, 50)], past, future);

        Assert.AreEqual(new ActivityPerformance(1m, 2m, 10m), Activity(report, sprint.Id).Performance);
    }

    [TestMethod]
    public void AnActivityTwiceInOneEvent_CountsThatEventOnce()
    {
        var twice = Event(Day(3, 1), sprint.Id, Pick(kart, sprint), Pick(kart, sprint));
        var once = Event(Day(4, 1), null, Pick(kart, sprint));

        var report = Compute([Metrics(twice, 10, 4), Metrics(once, 20, 1)], twice, once);

        // Counted twice per Selection it would be a win rate of 1/3, 3 Likes and 13.33 attendees.
        Assert.AreEqual(new ActivityPerformance(0.5m, 2.5m, 15m), Activity(report, sprint.Id).Performance);
    }

    [TestMethod]
    public void AMetricsItemForAnEventNotInTheList_IsIgnored()
    {
        var ev = Event(Day(3, 1), sprint.Id, Pick(kart, sprint));
        // Battle is offered at an Event in the list that has no metrics item of its own, so an unknown item that
        // landed on it would show as 50 Likes and 100 attendees.
        var noMetrics = Event(Day(4, 1), null, Pick(kart, battle));
        var unknown = new EventMetricsWithLikes(Guid.NewGuid(), 100, null, null, null, 50);

        var report = Compute([Metrics(ev, 10, 2), unknown], ev, noMetrics);

        Assert.AreEqual(new ActivityPerformance(1m, 2m, 10m), Activity(report, sprint.Id).Performance);
        Assert.AreEqual(new ActivityPerformance(0m, 0m, null), Activity(report, battle.Id).Performance);
    }
}
