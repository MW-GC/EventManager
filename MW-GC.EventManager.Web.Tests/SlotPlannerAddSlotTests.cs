using MW_GC.EventManager.Shared.Entities;
using static MW_GC.EventManager.Web.Tests.SlotTestData;

namespace MW_GC.EventManager.Web.Tests;

// Add Activity Slot: one new slot per click from the same pool a re-roll uses, refused with a reason when
// nothing is left. Fixture: SlotPlannerFixture.
[TestClass]
public class SlotPlannerAddSlotTests : SlotPlannerFixture
{
    [TestMethod]
    public void AddSlotSkipsExhaustedGames()
    {
        Unique(false);
        for (var i = 0; i < 100; i++)
        {
            planner.AddSlot();
            Assert.AreEqual(3, Slots.Count);
            Assert.AreEqual(replacement.Id, Slots[2].ActivityId);
            planner.SetSlots(Slots.Take(2));
        }
    }

    [TestMethod]
    public void UniqueGamesBlocksAddWhenOnlyUsedGamesHaveFreeActivities()
    {
        var library = UseLibrary(2, 1, 1);
        var free = Activities[1];
        SetSlots(Activities[0], Activities[2], Activities[3]);
        Unique(true);
        Assert.IsEmpty(planner.GetAddSlotCandidates());
        AssertAddSlotRefused("Every Game with an unused activity is already in a slot. Turn off Unique games only to repeat a Game.");

        Unique(false);
        Assert.IsNull(planner.GetAddSlotBlockedReason());
        Assert.AreEqual(free.Id, Assert.ContainsSingle(planner.GetAddSlotCandidates()).Id);
        planner.AddSlot();
        Assert.AreEqual(4, Slots.Count);
        Assert.AreEqual(free.Id, Slots[3].ActivityId);
        Assert.AreEqual(library[0].Id, Slots[3].GameId);
        Assert.IsNull(planner.AddSlotError);
        Assert.AreEqual(4, Slots.Select(s => s.ActivityId).Distinct().Count());
    }

    [TestMethod]
    [DataRow("_custGameIds", true)]
    [DataRow("_custGameIds", false)]
    [DataRow("_custThemeIds", true)]
    [DataRow("_custThemeIds", false)]
    [DataRow("_custHolidayIds", true)]
    [DataRow("_custHolidayIds", false)]
    public void FiltersExcludingEveryUnusedActivityDisableAddWithReason(string filter, bool unique)
    {
        var library = UseLibrary(1, 1, 1, 1);
        SetSlots(Activities[0], Activities[1], Activities[2]);
        Unique(unique);
        SetIds(filter, Guid.NewGuid());
        Assert.IsEmpty(planner.GetAddSlotCandidates());
        AssertAddSlotRefused(AddSlotNoMatchReason);

        // Clearing the filter frees the fourth Game: one click adds exactly that one slot.
        SetIds(filter);
        Assert.IsNull(planner.GetAddSlotBlockedReason());
        planner.AddSlot();
        Assert.AreEqual(4, Slots.Count);
        Assert.AreEqual(Activities[3].Id, Slots[3].ActivityId);
        Assert.AreEqual(library[3].Id, Slots[3].GameId);
        Assert.IsNull(planner.AddSlotError);
        Assert.IsNull(planner.GenerationError);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void AddSlotAddsExactlyOneUniqueSlotPerClickUntilTheLimit(bool unique)
    {
        UseLibrary(2, 2, 2, 2, 2, 2);
        Unique(unique);
        for (var run = 0; run < 50; run++)
        {
            SetSlots(Activities[0]);
            for (var expected = 2; expected <= EventEntity.MaximumSelections; expected++)
            {
                Assert.IsNull(planner.GetAddSlotBlockedReason());
                planner.AddSlot();
                Assert.AreEqual(expected, Slots.Count);
                var slots = Slots.ToArray();
                Assert.AreEqual(expected, slots.Select(s => s.ActivityId).Distinct().Count());
                if (unique) Assert.AreEqual(expected, slots.Select(s => s.GameId).Distinct().Count());
                foreach (var s in slots) Assert.Contains(a => a.Id == s.ActivityId && a.GameId == s.GameId, Activities);
            }
            AssertAddSlotRefused("An Event holds at most 5 activities.");
        }
    }

    [TestMethod]
    public void RerollAndAddSlotShareOneCandidatePool()
    {
        // Same filters, same used Activities, same Unique games rule: the slot being re-rolled
        // is the only difference, so removing it makes the re-roll pool the add pool.
        Activities.Add(Activity(gameA));
        Activities.Add(Activity(gameB));
        Activities.Add(Activity(Guid.NewGuid()));
        foreach (var unique in new[] { true, false })
        {
            Unique(unique);
            var rerollPool = Candidates().Select(a => a.Id).ToHashSet();
            var all = Slots.ToList();
            planner.SetSlots(all.Skip(1));
            var addPool = planner.GetAddSlotCandidates().Select(a => a.Id).ToHashSet();
            planner.SetSlots(all);
            addPool.Remove(current.Id);
            CollectionAssert.AreEquivalent(addPool.ToList(), rerollPool.ToList());
        }
    }

    // A blocked Add Activity Slot keeps every slot, keeps the generation error and says why.
    private void AssertAddSlotRefused(string expectedReason)
    {
        const string error = "Previous generation failure";
        planner.GenerationError = error;
        var original = Slots;
        var originalSlots = Slots.ToArray();
        Assert.AreEqual(expectedReason, planner.GetAddSlotBlockedReason());

        planner.AddSlot();

        Assert.AreSame(original, Slots);
        Assert.AreSequenceEqual(originalSlots, Slots.ToArray());
        Assert.AreEqual(expectedReason, planner.AddSlotError);
        Assert.AreEqual(error, planner.GenerationError);
    }
}
