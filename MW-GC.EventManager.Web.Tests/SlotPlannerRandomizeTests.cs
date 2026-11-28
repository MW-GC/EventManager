using MW_GC.EventManager.Shared.Entities;
using static MW_GC.EventManager.Web.Tests.SlotTestData;

namespace MW_GC.EventManager.Web.Tests;

// Randomize All: filling the slots, sizing a fresh roll to the library, applying every filter, and the
// messages when nothing can be drawn. Fixture: SlotPlannerFixture.
[TestClass]
public class SlotPlannerRandomizeTests : SlotPlannerFixture
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RandomizeAllFillsSlotsAndRerollsKeepIdsUnique(bool unique)
    {
        Unique(unique);
        for (var i = 0; i < 100; i++)
        {
            planner.RandomizeAll();
            Assert.AreEqual(2, Slots.Count);
            Assert.AreEqual(2, Slots.Select(s => s.ActivityId).Distinct().Count());
            var otherSlot = Slots[1];
            planner.RerollSlot(0);
            Assert.AreSame(otherSlot, Slots[1]);
            Assert.AreEqual(2, Slots.Select(s => s.ActivityId).Distinct().Count());
            Assert.IsNull(planner.GenerationError);
        }
    }

    [TestMethod]
    public void SameGameCanFillAllSlotsWithoutDuplicateIds()
    {
        Unique(false);
        SetIds("_custGameIds", gameA);
        Activities.Add(current); // Duplicate source records are not extra capacity.
        for (var i = 0; i < 100; i++)
        {
            planner.RandomizeAll();
            Assert.AreEqual(2, Slots.Count);
            foreach (var s in Slots) Assert.AreEqual(gameA, s.GameId);
            Assert.AreEqual(2, Slots.Select(s => s.ActivityId).Distinct().Count());
        }
        planner.AddSlot();
        Assert.AreEqual(2, Slots.Count);
    }

    [TestMethod]
    [DataRow("_custGameIds")]
    [DataRow("_custThemeIds")]
    [DataRow("_custHolidayIds")]
    public void ImpossibleRandomizePreservesSlotsAndReportsError(string filter)
    {
        var original = Slots;
        SetIds(filter, Guid.NewGuid());
        planner.RandomizeAll();
        Assert.AreSame(original, Slots);
        Assert.AreEqual(2, Slots.Count);
        Assert.IsFalse(string.IsNullOrWhiteSpace(planner.GenerationError));
        SetIds(filter);
        planner.RandomizeAll();
        Assert.IsNull(planner.GenerationError);
    }

    [TestMethod]
    public void InitialImpossibleGenerationShowsErrorRatherThanSilentlyBlank()
    {
        planner.SetSlots([]);
        Activities.Clear();
        Activities.Add(Activity(Guid.NewGuid()));
        planner.RandomizeAll();
        Assert.IsEmpty(Slots);
        Assert.IsFalse(string.IsNullOrWhiteSpace(planner.GenerationError));
    }

    [TestMethod]
    public void RandomizeAppliesAllFiltersTogether()
    {
        Unique(false);
        current.ThemeIds = [theme];
        current.HolidayIds = [holiday];
        Activities.Add(Activity(gameA));
        SetIds("_custGameIds", gameA);
        SetIds("_custThemeIds", theme);
        SetIds("_custHolidayIds", holiday);
        planner.Filter = planner.Filter with { ThemedOnly = true };
        planner.RandomizeAll();
        Assert.AreEqual(2, Slots.Count);
        foreach (var s in Slots) Assert.Contains(s.ActivityId, new[] { current.Id, replacement.Id });
        Assert.AreEqual(2, Slots.Select(s => s.ActivityId).Distinct().Count());
        Assert.IsNull(planner.GenerationError);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void OneSlotRandomizationAndRerollRespectFilters(bool unique)
    {
        planner.RemoveSlot(1);
        Unique(unique);
        SetIds("_custGameIds", gameA);
        SetIds("_custThemeIds", theme);
        SetIds("_custHolidayIds", holiday);
        planner.Filter = planner.Filter with { ThemedOnly = true };
        planner.RandomizeAll();
        Assert.ContainsSingle(Slots);
        Assert.AreEqual(replacement.Id, Slots[0].ActivityId);
        Assert.IsEmpty(Candidates());
    }

    [TestMethod]
    [DataRow(1, 1)]
    [DataRow(2, 2)]
    [DataRow(5, 3)]
    public void FreshRandomizeWithRepeatedGamesAllowedSizesSlotsToTheActivities(int activityCount, int expectedSlots)
    {
        UseLibrary(activityCount);
        planner.SetSlots([]);
        Unique(false);
        planner.RandomizeAll();
        AssertFilledSlots(expectedSlots, distinctGames: false);
    }

    [TestMethod]
    [DataRow("_custGameIds", true)]
    [DataRow("_custGameIds", false)]
    [DataRow("_custThemeIds", true)]
    [DataRow("_custThemeIds", false)]
    [DataRow("_custHolidayIds", true)]
    [DataRow("_custHolidayIds", false)]
    public void FreshRandomizeWithFiltersExcludingEverythingReportsNoMatch(string filter, bool unique)
    {
        UseLibrary(1, 1, 1);
        planner.SetSlots([]);
        Unique(unique);
        SetIds(filter, Guid.NewGuid());
        planner.RandomizeAll();
        Assert.IsEmpty(Slots);
        Assert.AreEqual(NoMatchMessage, planner.GenerationError);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RandomizeAllWithExistingSelectionsKeepsThemAndSaysSo(bool unique)
    {
        var library = UseLibrary(1, 1, 1);
        SetSlots(Activities.ToArray());
        Unique(unique);
        SetIds("_custGameIds", library[0].Id, library[1].Id);
        var original = Slots;
        var originalSlots = Slots.ToArray();
        planner.RandomizeAll();
        Assert.AreSame(original, Slots);
        Assert.AreSequenceEqual(originalSlots, Slots.ToArray());
        Assert.Contains("Existing selections were kept", planner.GenerationError!);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RandomizeAllAcceptsUpperBoundaryWithoutRepeatingActivities(bool unique)
    {
        // With repeated Games allowed, use one Game so all five slots must share it.
        UseLibrary(unique ? new[] { 1, 1, 1, 1, 1 } : new[] { 5 });
        Unique(unique);
        for (var i = 0; i < 100; i++)
        {
            SetSlots(Enumerable.Repeat(Activities[0], EventEntity.MaximumSelections).ToArray());
            planner.RandomizeAll();
            AssertFilledSlots(EventEntity.MaximumSelections, distinctGames: unique);
        }
    }

    // Added with the extraction: until both library lists have loaded, nothing can be picked.
    [TestMethod]
    public void LibraryNotLoadedYetOffersNothingAndRandomizeReportsNoMatch()
    {
        var unloaded = new (List<GameEntity>? Games, List<ActivityEntity>? Activities)[] { (games, null), (null, activities), (null, null) };
        foreach (var (g, a) in unloaded)
        {
            planner.SetLibrary(g, a);
            Assert.IsEmpty(Candidates());
            Assert.IsEmpty(planner.GetAddSlotCandidates());
            Assert.IsEmpty(planner.GetAvailableGames(0));
            Assert.IsEmpty(planner.GetAvailableActivities(gameA, 0));
            Assert.AreEqual(AddSlotNoMatchReason, planner.GetAddSlotBlockedReason());
        }
        planner.SetSlots([]);
        planner.RandomizeAll();
        Assert.IsEmpty(Slots);
        Assert.AreEqual(NoMatchMessage, planner.GenerationError);
    }
}
