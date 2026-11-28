using MW_GC.EventManager.Shared.Entities;
using static MW_GC.EventManager.Web.Tests.SlotTestData;

namespace MW_GC.EventManager.Web.Tests;

// Scenarios carried over from the API generator tests that #48 removed: the planner is the
// only place that draws slots, so it must keep these rules. Fixture: SlotPlannerFixture.
[TestClass]
public class SlotPlannerPoolTests : SlotPlannerFixture
{
    [TestMethod]
    public void RepeatedGamesExhaustPoolWithoutRepeatingIds()
    {
        var library = UseLibrary(5);
        Activities.Add(Activities[0]); // Duplicate records do not add capacity.
        var source = Activities.ToArray();
        Unique(false);
        for (var i = 0; i < 100; i++)
        {
            SetSlots(Enumerable.Repeat(Activities[0], 5).ToArray());
            planner.RandomizeAll();
            AssertFilledSlots(5, distinctGames: false);
            foreach (var s in Slots) Assert.AreEqual(library[0].Id, s.GameId);
        }
        Assert.AreSequenceEqual(source, Activities.ToArray());

        // Four distinct ids plus the duplicate cannot fill five slots.
        Activities.Remove(source[4]);
        var original = Slots;
        var originalSlots = Slots.ToArray();
        planner.RandomizeAll();
        Assert.AreSame(original, Slots);
        Assert.AreSequenceEqual(originalSlots, Slots.ToArray());
        Assert.IsNotNull(planner.GenerationError);

        // One Game cannot fill two slots with Unique games only on.
        SetSlots(source[0], source[1]);
        Unique(true);
        planner.GenerationError = null;
        original = Slots;
        planner.RandomizeAll();
        Assert.AreSame(original, Slots);
        Assert.IsNotNull(planner.GenerationError);
    }

    [TestMethod]
    public void SkewedPoolAlwaysFillsEverySlot()
    {
        // One Game holds almost every Activity; four Games hold one each.
        UseLibrary(50, 1, 1, 1, 1);
        Unique(false);
        for (var i = 0; i < 100; i++)
        {
            SetSlots(Enumerable.Repeat(Activities[0], 5).ToArray());
            planner.RandomizeAll();
            AssertFilledSlots(5, distinctGames: false);
            foreach (var s in Slots)
                Assert.Contains(a => a.Id == s.ActivityId && a.GameId == s.GameId, Activities);
        }
    }

    [TestMethod]
    public void SkewedPoolExhaustsEveryDistinctIdWithoutMutatingInputs()
    {
        // Five distinct ids, two of them on one Game, plus duplicate records of that Game's ids.
        var library = UseLibrary(2, 1, 1, 1);
        var expected = Activities.Select(a => a.Id).ToArray();
        Activities.AddRange(Activities.Take(2).ToArray());
        var source = Activities.ToArray();
        var originalGames = library.ToArray();
        Unique(false);

        // Reuse the same planner and inputs to catch state leaking between draws.
        for (var run = 0; run < 50; run++)
        {
            SetSlots(Enumerable.Repeat(Activities[0], 5).ToArray());
            planner.RandomizeAll();
            AssertFilledSlots(5, distinctGames: false);
            CollectionAssert.AreEquivalent(expected, Slots.Select(s => s.ActivityId).ToArray());
            foreach (var s in Slots)
                Assert.Contains(a => a.Id == s.ActivityId && a.GameId == s.GameId, Activities);
            Assert.AreSequenceEqual(source, Activities.ToArray());
            Assert.AreSequenceEqual(originalGames, games.ToArray());
        }
    }

    [TestMethod]
    public void DuplicateIdsAcrossPoolsAreRemovedTogetherIncludingExhaustedGames()
    {
        var library = UseLibrary(Enumerable.Repeat(0, 10).ToArray());
        var (one, two, three) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        // Ids one and two appear in every Game (one twice per Game); id three only in the first.
        var pool = library.SelectMany(g => new[]
        {
            new ActivityEntity { Id = one, GameId = g.Id },
            new ActivityEntity { Id = one, GameId = g.Id },
            new ActivityEntity { Id = two, GameId = g.Id },
        }).ToList();
        pool.Add(new ActivityEntity { Id = three, GameId = library[0].Id });
        SetActivities(pool);
        Unique(false);

        for (var run = 0; run < 20; run++)
        {
            SetSlots(pool[0], pool[0], pool[0]);
            planner.RandomizeAll();
            AssertFilledSlots(3, distinctGames: false);
            CollectionAssert.AreEquivalent(new[] { one, two, three }, Slots.Select(s => s.ActivityId).ToArray());
            foreach (var s in Slots)
                Assert.Contains(a => a.Id == s.ActivityId && a.GameId == s.GameId, pool);
        }

        // Three distinct ids cannot fill four slots, however many Games repeat them.
        SetSlots(pool[0], pool[0], pool[0], pool[0]);
        var original = Slots;
        planner.RandomizeAll();
        Assert.AreSame(original, Slots);
        Assert.IsNotNull(planner.GenerationError);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void IndexedPoolsRespectCombinedFiltersAndIgnoreOrphanCapacity(bool unique)
    {
        var orphan = Guid.NewGuid(); // Selected in the Game filter, but no such Game exists.
        ActivityEntity Tagged(Guid game, bool withTheme, bool withHoliday)
        {
            var a = Activity(game);
            if (withTheme) a.ThemeIds = [theme];
            if (withHoliday) a.HolidayIds = [holiday];
            return a;
        }
        var eligible = Tagged(gameA, true, true);
        var wrongGame = Tagged(gameB, true, true);
        var orphanActivity = Tagged(orphan, true, true); // Passes every filter but has no known Game.
        var themeOnly = Tagged(gameA, true, false);
        var holidayOnly = Tagged(gameA, false, true);
        var unthemed = Tagged(gameA, false, false);
        SetActivities([eligible, wrongGame, orphanActivity, themeOnly, holidayOnly, unthemed]);
        Unique(unique);
        planner.Filter = planner.Filter with { ThemedOnly = true };
        SetIds("_custGameIds", gameA, orphan);
        SetIds("_custThemeIds", theme);
        SetIds("_custHolidayIds", holiday);

        SetSlots(current);
        planner.RandomizeAll();
        AssertFilledSlots(1, distinctGames: true);
        Assert.AreEqual(eligible.Id, Slots[0].ActivityId);
        Assert.AreEqual(gameA, Slots[0].GameId);

        // The orphan's Activity must not add capacity ...
        SetSlots(current, other);
        var original = Slots;
        planner.RandomizeAll();
        Assert.AreSame(original, Slots);
        Assert.IsNotNull(planner.GenerationError);

        // ... and no Games means no capacity at all.
        var library = games;
        SetGames([]);
        SetSlots(current);
        original = Slots;
        planner.RandomizeAll();
        Assert.AreSame(original, Slots);
        Assert.IsNotNull(planner.GenerationError);
        SetGames(library);

        // Dropping the Theme filter admits the holiday-only Activity (themed via its Holiday).
        SetActivities([holidayOnly]);
        SetIds("_custThemeIds");
        planner.RandomizeAll();
        AssertFilledSlots(1, distinctGames: true);
        Assert.AreEqual(holidayOnly.Id, Slots[0].ActivityId);

        // An unthemed Activity stays excluded while Themed activities only is on ...
        SetActivities([unthemed]);
        SetIds("_custHolidayIds");
        original = Slots;
        planner.RandomizeAll();
        Assert.AreSame(original, Slots);
        Assert.IsNotNull(planner.GenerationError);

        // ... and is admitted once every Filter is cleared.
        planner.Filter = planner.Filter with { ThemedOnly = false };
        SetIds("_custGameIds");
        planner.RandomizeAll();
        AssertFilledSlots(1, distinctGames: true);
        Assert.AreEqual(unthemed.Id, Slots[0].ActivityId);
    }
}
