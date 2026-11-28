using MW_GC.EventManager.Shared.Entities;
using static MW_GC.EventManager.Web.Tests.SlotTestData;

namespace MW_GC.EventManager.Web.Tests;

// Re-rolling one slot, and changing or removing a slot by hand: the candidate pool, the filters it honours,
// and the no-op cases that keep every slot. Fixture: SlotPlannerFixture.
[TestClass]
public class SlotPlannerTests : SlotPlannerFixture
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void NeverDuplicatesActivitiesOrReturnsCurrent(bool unique)
    {
        Unique(unique);
        Assert.AreSequenceEqual(new[] { replacement.Id }, Candidates().Select(a => a.Id));
    }

    [TestMethod]
    public void UniqueGamesExcludesGamesUsedByOtherSlots()
    {
        var alternative = Activity(gameB);
        Activities.Add(alternative);
        Assert.DoesNotContain(alternative, Candidates());
        Unique(false);
        Assert.Contains(alternative, Candidates());
    }

    [TestMethod]
    [DataRow("_custGameIds")]
    [DataRow("_custThemeIds")]
    [DataRow("_custHolidayIds")]
    public void SelectedFiltersAreApplied(string field)
    {
        SetIds(field, field == "_custGameIds" ? gameA : field == "_custThemeIds" ? theme : holiday);
        Assert.ContainsSingle(Candidates());
        SetIds(field, Guid.NewGuid());
        Assert.IsEmpty(Candidates());
        var original = Slots[0];
        planner.RerollSlot(0);
        Assert.AreSame(original, Slots[0]);
    }

    [TestMethod]
    public void ThemedOnlyAcceptsThemeOrHolidayAndRejectsUntagged()
    {
        var untagged = Activity(gameA);
        Activities.Add(untagged);
        planner.Filter = planner.Filter with { ThemedOnly = true };
        Assert.DoesNotContain(untagged, Candidates());
        replacement.ThemeIds.Clear();
        Assert.Contains(replacement, Candidates());
        replacement.HolidayIds.Clear();
        Assert.IsEmpty(Candidates());
    }

    [TestMethod]
    public void FiltersCombineRatherThanOverrideEachOther()
    {
        SetIds("_custGameIds", gameA);
        SetIds("_custThemeIds", theme);
        SetIds("_custHolidayIds", holiday);
        Assert.ContainsSingle(Candidates());
        replacement.HolidayIds.Clear();
        Assert.IsEmpty(Candidates());
    }

    [TestMethod]
    public void ExhaustedPoolIsNoOpAndIgnoresOrphanedActivities()
    {
        Activities.Remove(replacement);
        Activities.Add(Activity(Guid.NewGuid()));
        var original = Slots[0];
        Assert.IsEmpty(Candidates());
        planner.RerollSlot(0);
        Assert.AreSame(original, Slots[0]);
        Assert.AreEqual(2, Slots.Count);
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(2)]
    public void InvalidIndexIsNoOp(int index)
    {
        Assert.IsEmpty(planner.GetRerollCandidates(index));
        planner.RerollSlot(index);
        Assert.AreEqual(current.Id, Slots[0].ActivityId);
    }

    [TestMethod]
    public void RepeatedRollsKeepOtherSlotsAndStayInEligiblePool()
    {
        Activities.Add(Activity(gameA));
        var originalOther = Slots[1];
        for (var i = 0; i < 100; i++)
        {
            var before = Slots[0].ActivityId;
            var eligible = Candidates().Select(a => a.Id).ToList();
            planner.RerollSlot(0);
            var after = Slots[0].ActivityId;
            Assert.AreNotEqual(before, after);
            Assert.Contains(after, eligible);
            Assert.AreSame(originalOther, Slots[1]);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ExhaustedGameIsUnavailableAndChoosingItPreservesSlotsAndError(bool unique)
    {
        Unique(unique);
        var exhausted = new GameEntity { Id = Guid.NewGuid() };
        games.Add(exhausted);
        Activities.Add(new ActivityEntity { GameId = exhausted.Id, Id = other.Id });
        const string error = "Previous generation failure";
        planner.GenerationError = error;
        var original = Slots;
        var originalSlots = Slots.ToArray();

        var available = planner.GetAvailableGames(0);
        Assert.DoesNotContain(g => g.Id == exhausted.Id, available);
        Assert.Contains(g => g.Id == gameA, available);
        Assert.DoesNotContain(a => a.GameId == exhausted.Id, Candidates());

        // Defend against a stale dropdown callback as well as filtering options.
        planner.ChangeSlotGame(0, exhausted);
        Assert.AreSame(original, Slots);
        Assert.AreSame(originalSlots[0], Slots[0]);
        Assert.AreSame(originalSlots[1], Slots[1]);
        Assert.AreEqual(error, planner.GenerationError);

        // Once a real assignment is available, change only this slot and clear the error.
        var unused = Activity(exhausted.Id);
        Activities.Add(unused);
        Assert.Contains(g => g.Id == exhausted.Id, planner.GetAvailableGames(0));
        planner.ChangeSlotGame(0, exhausted);
        Assert.AreEqual(exhausted.Id, Slots[0].GameId);
        Assert.AreEqual(unused.Id, Slots[0].ActivityId);
        Assert.AreSame(originalSlots[1], Slots[1]);
        Assert.AreEqual(2, Slots.Count);
        Assert.IsNull(planner.GenerationError);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RerollWithOnlyExhaustedGamesPreservesSlotsAndError(bool unique)
    {
        Unique(unique);
        Activities.Remove(replacement);
        var exhausted = new GameEntity { Id = Guid.NewGuid() };
        games.Add(exhausted);
        Activities.Add(new ActivityEntity { GameId = exhausted.Id, Id = other.Id });
        const string error = "Previous generation failure";
        planner.GenerationError = error;
        var original = Slots;
        var originalSlots = Slots.ToArray();

        Assert.IsEmpty(Candidates());
        planner.RerollSlot(0);

        Assert.AreSame(original, Slots);
        Assert.AreSame(originalSlots[0], Slots[0]);
        Assert.AreSame(originalSlots[1], Slots[1]);
        Assert.AreEqual(2, Slots.Count);
        Assert.AreEqual(error, planner.GenerationError);
    }

    [TestMethod]
    public void RemoveAllowsOneSlotButNeverRemovesLastSlot()
    {
        planner.RemoveSlot(1);
        Assert.ContainsSingle(Slots);
        planner.RemoveSlot(0);
        Assert.ContainsSingle(Slots);
        Assert.AreEqual(current.Id, Slots[0].ActivityId);
    }
}
