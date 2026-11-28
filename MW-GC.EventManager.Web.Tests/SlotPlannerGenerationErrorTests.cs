using MW_GC.EventManager.Shared.Entities;
using static MW_GC.EventManager.Web.Tests.SlotTestData;

namespace MW_GC.EventManager.Web.Tests;

// The generation error: cleared by every change to the slots, kept by every call that changes nothing,
// and kept across failed Randomize All retries. Fixture: SlotPlannerFixture.
[TestClass]
public class SlotPlannerGenerationErrorTests : SlotPlannerFixture
{
    // The page rows of this test (ShowEditEvent, ShowCustomize) stay in SlotRerollTests.
    [TestMethod]
    [DataRow("RandomizeAll", true)]
    [DataRow("RandomizeAll", false)]
    [DataRow("RerollSlot", true)]
    [DataRow("ChangeSlotGame", true)]
    [DataRow("ChangeSlotActivity", true)]
    [DataRow("AddSlot", false)]
    [DataRow("RemoveSlot", true)]
    public void SuccessfulSelectionMutationClearsStaleGenerationError(string handler, bool unique)
    {
        UseRandom(new FirstChoiceRandom());
        Unique(unique);
        planner.GenerationError = "Previous generation failure";
        if (handler == "RemoveSlot") AddSlot(replacement);

        switch (handler)
        {
            case "RerollSlot":
                planner.RerollSlot(0);
                Assert.AreEqual(replacement.Id, Slots[0].ActivityId);
                break;
            case "ChangeSlotGame":
                var newGame = new GameEntity { Id = Guid.NewGuid() };
                var newActivity = Activity(newGame.Id);
                games.Add(newGame);
                Activities.Add(newActivity);
                planner.ChangeSlotGame(0, newGame);
                Assert.AreEqual(newGame.Id, Slots[0].GameId);
                Assert.AreEqual(newActivity.Id, Slots[0].ActivityId);
                break;
            case "ChangeSlotActivity":
                planner.ChangeSlotActivity(0, replacement);
                Assert.AreEqual(replacement.Id, Slots[0].ActivityId);
                break;
            default:
                if (handler == "RemoveSlot") planner.RemoveSlot(2);
                else if (handler == "AddSlot") planner.AddSlot();
                else planner.RandomizeAll();
                Assert.AreEqual(handler is "AddSlot" ? 3 : 2, Slots.Count);
                break;
        }
        Assert.IsNull(planner.GenerationError);
    }

    [TestMethod]
    [DataRow("RerollSlot")]
    [DataRow("InvalidReroll")]
    [DataRow("ChangeSlotGame")]
    [DataRow("ChangeSlotActivity")]
    [DataRow("AddSlot")]
    [DataRow("FullAddSlot")]
    [DataRow("RemoveSlot")]
    public void UnchangedSelectionsKeepGenerationError(string handler)
    {
        const string error = "Previous generation failure";
        planner.GenerationError = error;
        if (handler == "RerollSlot") Activities.Remove(replacement);
        if (handler == "FullAddSlot")
            while (Slots.Count < 5) AddSlot(replacement);
        // RemoveSlot keeps a floor of one slot, so the no-op case needs exactly one slot.
        if (handler == "RemoveSlot") planner.SetSlots(Slots.Take(1));
        var original = Slots;
        var originalSlots = Slots.ToArray();

        switch (handler)
        {
            case "RerollSlot": planner.RerollSlot(0); break;
            case "RemoveSlot": planner.RemoveSlot(0); break;
            case "InvalidReroll": planner.RerollSlot(-1); break;
            case "ChangeSlotGame": planner.ChangeSlotGame(0, null); break;
            case "ChangeSlotActivity": planner.ChangeSlotActivity(0, null); break;
            default: planner.AddSlot(); break;
        }

        Assert.AreEqual(error, planner.GenerationError);
        Assert.AreSame(original, Slots);
        Assert.AreSequenceEqual(originalSlots, Slots.ToArray());
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void FailedRandomizeKeepsSelectionsAndErrorUntilSuccessfulRetry(bool unique)
    {
        UseRandom(new FirstChoiceRandom());
        Unique(unique);
        SetIds("_custGameIds", gameB);
        var original = Slots;
        var originalSlots = Slots.ToArray();
        planner.RandomizeAll();
        var error = planner.GenerationError;
        Assert.IsNotNull(error);
        planner.RandomizeAll();
        Assert.AreEqual(error, planner.GenerationError);
        Assert.AreSame(original, Slots);
        Assert.AreSequenceEqual(originalSlots, Slots.ToArray());

        SetIds("_custGameIds");
        planner.RandomizeAll();
        Assert.IsNull(planner.GenerationError);
        Assert.AreEqual(2, Slots.Count);
    }
}
