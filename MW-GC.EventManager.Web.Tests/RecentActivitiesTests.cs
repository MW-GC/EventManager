using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Tests;

// #95: the Activities of the last N Events dated before the Event being planned, worked out from plain inputs (the
// Events list, the planned instant, the edited Event's id and N). The planner gets only the ids this returns.
[TestClass]
public sealed class RecentActivitiesTests
{
    private static readonly DateTimeOffset Planned = new(2026, 11, 20, 19, 0, 0, TimeSpan.Zero);
    private readonly Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), d = Guid.NewGuid(), e = Guid.NewGuid();

    [TestMethod]
    [DataRow(1, DisplayName = "N 1")]
    [DataRow(2, DisplayName = "N 2")]
    [DataRow(3, DisplayName = "N 3")]
    public void TheLastNEventsByDate_AreRecent(int n)
    {
        // Listed out of date order: the newest three are c (1 day before), b (2 days), a (3 days); d is the oldest.
        var events = new[] { At(-10, d), At(-2, b), At(-1, c), At(-3, a) };

        var recent = RecentActivities.Ids(events, Planned, editing: null, n);

        var expected = new[] { c, b, a }.Take(n);
        CollectionAssert.AreEquivalent(expected.ToList(), recent.ToList());
    }

    [TestMethod]
    public void AnEventWithTwoSelectionsOfTheSameActivity_CountsOnce()
    {
        // The newer Event offers a twice; it still uses up one of the two places, so b is recent too.
        var events = new[] { At(-1, a, a), At(-2, b), At(-3, c) };

        var recent = RecentActivities.Ids(events, Planned, editing: null, 2);

        CollectionAssert.AreEquivalent(new[] { a, b }, recent.ToList());
    }

    [TestMethod]
    public void EveryActivityOfARecentEvent_IsRecent()
    {
        var events = new[] { At(-1, a, b, c), At(-2, d) };

        var recent = RecentActivities.Ids(events, Planned, editing: null, 1);

        CollectionAssert.AreEquivalent(new[] { a, b, c }, recent.ToList());
    }

    [TestMethod]
    public void EventsAtOrAfterThePlannedInstant_AreNotRecent()
    {
        var events = new[]
        {
            new EventEntity { Name = "at", Date = Planned, Selections = [Sel(a)] },
            new EventEntity { Name = "one second later", Date = Planned.AddSeconds(1), Selections = [Sel(b)] },
            new EventEntity { Name = "one second before", Date = Planned.AddSeconds(-1), Selections = [Sel(c)] },
            At(+3, d)
        };

        var recent = RecentActivities.Ids(events, Planned, editing: null, 10);

        CollectionAssert.AreEquivalent(new[] { c }, recent.ToList());
    }

    // The same instant written in another offset is the same instant.
    [TestMethod]
    public void ThePlannedInstant_IsComparedAsAnInstant()
    {
        var sameInstantElsewhere = new EventEntity { Name = "same", Date = Planned.ToOffset(TimeSpan.FromHours(-5)), Selections = [Sel(a)] };
        var earlierElsewhere = new EventEntity { Name = "earlier", Date = Planned.AddMinutes(-1).ToOffset(TimeSpan.FromHours(9)), Selections = [Sel(b)] };

        var recent = RecentActivities.Ids([sameInstantElsewhere, earlierElsewhere], Planned, editing: null, 10);

        CollectionAssert.AreEquivalent(new[] { b }, recent.ToList());
    }

    [TestMethod]
    public void TheEditedEvent_IsExcluded_AndDoesNotUseUpAPlace()
    {
        var edited = At(-1, a);
        var events = new[] { edited, At(-2, b), At(-3, c) };

        var recent = RecentActivities.Ids(events, Planned, editing: edited.Id, 1);

        CollectionAssert.AreEquivalent(new[] { b }, recent.ToList());
    }

    [TestMethod]
    public void AnEventWithNoSelections_DoesNotUseUpAPlace()
    {
        var events = new[] { At(-1), At(-2, b), At(-3, c) };

        var recent = RecentActivities.Ids(events, Planned, editing: null, 1);

        CollectionAssert.AreEquivalent(new[] { b }, recent.ToList());
    }

    [TestMethod]
    public void NLargerThanTheHistory_TakesEveryEarlierEvent()
    {
        var events = new[] { At(-1, a), At(-2, b), At(+1, e) };

        var recent = RecentActivities.Ids(events, Planned, editing: null, 10);

        CollectionAssert.AreEquivalent(new[] { a, b }, recent.ToList());
    }

    [TestMethod]
    public void NoEvents_NothingIsRecent()
    {
        Assert.IsEmpty(RecentActivities.Ids([], Planned, editing: null, 3));
    }

    // An Event dated the given number of days from the planned instant, offering these Activities.
    private static EventEntity At(int days, params Guid[] activities) => new()
    {
        Name = $"day {days}",
        Date = Planned.AddDays(days),
        Selections = activities.Select(Sel).ToList()
    };

    private static Selection Sel(Guid activity)
    {
        var game = Guid.NewGuid();
        return new Selection
        {
            Game = new Game { Id = game, Name = "G" },
            Activity = new Activity { Id = activity, GameId = game, Name = "A" }
        };
    }
}
