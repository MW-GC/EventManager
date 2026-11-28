using Bunit;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using static MW_GC.EventManager.Web.Tests.SlotTestData;

namespace MW_GC.EventManager.Web.Tests;

// The Events page's create-and-edit dialog over its planner: re-rolling a slot, Create Event's fresh roll and
// its generation error, and a blocked Add Activity Slot. Fixture: SlotPageFixture.
[TestClass]
public class SlotRerollTests : SlotPageFixture
{
    [TestMethod]
    public void RerollChangesOnlyTargetAndPreservesMetadata()
    {
        Set("_custName", "Keep this name");
        var date = new DateTime(2026, 9, 21);
        Set("_custDate", date);
        var originalOther = Slots[1];
        Planner.RerollSlot(0);
        Assert.AreEqual(replacement.Id, Id(Slots[0]!, "ActivityId"));
        Assert.AreEqual(gameA, Id(Slots[0]!, "GameId"));
        Assert.AreSame(originalOther, Slots[1]);
        Assert.AreEqual(2, Slots.Count);
        Assert.AreEqual("Keep this name", Get("_custName"));
        Assert.AreEqual(date, Get("_custDate"));
    }

    // The planner rows of this test (RandomizeAll, RerollSlot, ...) are in SlotPlannerGenerationErrorTests.
    [TestMethod]
    [DataRow("ShowEditEvent", true)]
    [DataRow("ShowCustomize", true)]
    public void SuccessfulSelectionMutationClearsStaleGenerationError(string handler, bool unique)
    {
        Set("_custUniqueGames", unique);
        UseRandom(new FirstChoiceRandom());
        Planner.GenerationError = "Previous generation failure";
        if (handler == "ShowCustomize")
        {
            var game = new GameEntity { Id = Guid.NewGuid() };
            ((List<GameEntity>)Get("_games")!).Add(game);
            Activities.Add(Activity(game.Id));
        }

        switch (handler)
        {
            case "ShowEditEvent":
                Call(handler, new EventEntity { Name = "Existing event", Selections = [] });
                Assert.IsEmpty(Slots);
                break;
            default:
                Call(handler);
                Assert.AreEqual(3, Slots.Count);
                break;
        }
        Assert.IsNull(Planner.GenerationError);
    }

    [TestMethod]
    public void FailedCustomizeGenerationRetainsError()
    {
        Activities.Clear();
        Planner.GenerationError = "Previous failure";
        Call("ShowCustomize");
        Assert.IsNotNull(Planner.GenerationError);
        Assert.IsEmpty(Slots);
        Assert.AreEqual(true, Get("_showCustomize"));
    }

    [TestMethod]
    [DataRow(1, 1, 1)]
    [DataRow(2, 1, 2)]
    [DataRow(2, 2, 2)]
    [DataRow(1, 2, 1)]
    [DataRow(3, 1, 3)]
    [DataRow(5, 1, 3)]
    [DataRow(8, 2, 3)]
    public void FreshCustomizeSizesUniqueSlotsToTheGamesTheLibraryCanFill(int games, int activitiesPerGame, int expectedSlots)
    {
        UseLibrary(Enumerable.Repeat(activitiesPerGame, games).ToArray());
        Planner.GenerationError = "Previous failure";
        Call("ShowCustomize");
        AssertFilledSlots(expectedSlots, distinctGames: true);
        Assert.AreEqual(true, Get("_showCustomize"));
    }

    // Rendered with bUnit and driven by a click: two Games and no Activities in the library.
    [TestMethod]
    public void FreshCustomizeWithNoActivitiesReportsNoMatchRatherThanKeptSelections()
    {
        var api = new StubApiHandler()
            .Json(HttpMethod.Get, "/api/games", new[] { new GameEntity { Name = "Kart" }, new GameEntity { Name = "Brawl" } })
            .Json(HttpMethod.Get, "/api/activities", Array.Empty<ActivityEntity>())
            .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
            .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>())
            .Json(HttpMethod.Get, "/api/events", new[] { new EventEntity { Name = "Game night" } });
        using var harness = new PageHarness(api);
        var rendered = harness.Render<Events>();
        rendered.WaitForRows(1);

        rendered.Button("Create Event").Click();

        Assert.IsTrue(rendered.HasFormDialog());
        var dialog = rendered.FormDialog();
        Assert.IsEmpty(dialog.QuerySelectorAll("fluent-button[aria-label^='Re-roll slot']"));
        Assert.Contains("Game & Activity Selections (0)", dialog.TextContent);
        Assert.AreEqual(NoMatchMessage, Assert.ContainsSingle(dialog.Alerts()));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void AddSlotWithEveryActivityInASlotIsDisabledWithReason(bool unique)
    {
        // Three Games of one Activity each: Create Event fills all three and nothing is left.
        UseLibrary(1, 1, 1);
        Call("ShowCustomize");
        AssertFilledSlots(3, distinctGames: true);
        Set("_custUniqueGames", unique);
        Assert.IsEmpty(AddSlotCandidates());
        AssertAddSlotRefused(AddSlotNoMatchReason);
    }

    // Scenarios carried over from the API generator tests that #48 removed: the page is now the
    // only place that draws slots, so it must keep these rules.

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RepeatedGameIdsAreToleratedFirstGameWins(bool unique)
    {
        var first = new GameEntity { Id = Guid.NewGuid(), Name = "First" };
        var impostor = new GameEntity { Id = first.Id, Name = "Same id, listed later" };
        var activity = Activity(first.Id);
        Set("_games", new List<GameEntity> { first, impostor });
        Set("_activities", new List<ActivityEntity> { activity });
        Set("_custUniqueGames", unique);
        SetSlots(current);

        Planner.RandomizeAll();

        AssertFilledSlots(1, distinctGames: true);
        Assert.AreEqual(activity.Id, Id(Slots[0]!, "ActivityId"));
        Assert.AreEqual(first.Id, Id(Slots[0]!, "GameId"));
        // The saved snapshot takes the first Game listed with that id.
        var handler = ConfigureSave();
        await Save();
        var selection = Assert.ContainsSingle(handler.Saved!.Selections);
        Assert.AreEqual("First", selection.Game.Name);
        Assert.AreEqual(activity.Id, selection.Activity.Id);
    }

    private string? AddSlotBlockedReason() => Planner.GetAddSlotBlockedReason();
    private List<ActivityEntity> AddSlotCandidates() => Planner.GetAddSlotCandidates();

    // A blocked Add Activity Slot keeps every slot, keeps the generation error and says why.
    private void AssertAddSlotRefused(string expectedReason)
    {
        const string error = "Previous generation failure";
        Planner.GenerationError = error;
        var original = Slots;
        var originalSlots = Slots.Cast<object>().ToArray();
        Assert.AreEqual(expectedReason, AddSlotBlockedReason());

        Planner.AddSlot();

        Assert.AreSame(original, Slots);
        Assert.AreSequenceEqual(originalSlots, Slots.Cast<object>().ToArray());
        Assert.AreEqual(expectedReason, Planner.AddSlotError);
        Assert.AreEqual(error, Planner.GenerationError);
    }
}
