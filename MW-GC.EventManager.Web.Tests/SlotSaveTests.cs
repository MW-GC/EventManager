using MW_GC.EventManager.Shared.Entities;
using static MW_GC.EventManager.Web.Tests.SlotTestData;

namespace MW_GC.EventManager.Web.Tests;

// Saving the create-and-edit dialog: which slot sets a save refuses before any request, and the Winner the
// saved Event carries and the page reads back. Fixture: SlotPageFixture.
[TestClass]
public class SlotSaveTests : SlotPageFixture
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(6)]
    public async Task InvalidSlotCountDoesNotSendRequest(int count)
    {
        Planner.SetSlots([]);
        // Every slot is otherwise valid, isolating the count guard from duplicate guards.
        Set("_custUniqueGames", false);
        for (var i = 0; i < count; i++)
        {
            var activity = Activity(gameA);
            Activities.Add(activity);
            AddSlot(activity);
        }
        var handler = ConfigureSave();
        await Save();
        Assert.IsNull(handler.LastWrite);
        Assert.Contains("Select 1–5", (string)Get("_customizeError")!);
        Assert.AreEqual(true, Get("_showCustomize"));
        Assert.AreEqual(false, Get("_generating"));
    }

    [TestMethod]
    public async Task IncompleteSlotIsNotSilentlyDroppedIntoAnAutomaticWinner()
    {
        Slots[1].ActivityId = Guid.Empty;
        var handler = ConfigureSave();
        await Save();
        Assert.IsNull(handler.LastWrite);
        Assert.Contains("every slot", (string)Get("_customizeError")!);
        Assert.AreEqual(2, Slots.Count);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task SameGameSelectionsRequireUniqueGamesToBeDisabled(bool unique)
    {
        Planner.SetSlots([]);
        AddSlot(current);
        AddSlot(replacement);
        Set("_custUniqueGames", unique);
        var handler = ConfigureSave();
        await Save();
        if (unique)
        {
            Assert.IsNull(handler.Saved);
            Assert.Contains("distinct", (string)Get("_customizeError")!);
        }
        else
        {
            Assert.AreEqual(2, handler.Saved!.Selections.Count);
            Assert.IsFalse(handler.Saved.UniqueGamesOnly);
            Assert.IsNull(handler.Saved.WinnerActivityId);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task DuplicateActivityIsRejectedEvenWhenRepeatedGamesAreAllowed(bool unique)
    {
        Planner.SetSlots([]);
        AddSlot(current);
        AddSlot(current);
        Set("_custUniqueGames", unique);
        var handler = ConfigureSave();
        await Save();
        Assert.IsNull(handler.LastWrite);
        Assert.IsNull(handler.Saved);
        Assert.AreEqual(0, handler.Reads);
        Assert.Contains("distinct", (string)Get("_customizeError")!);
        Assert.AreEqual(true, Get("_showCustomize"));
    }

    [TestMethod]
    public async Task OneSlotCreateAndEditSendAutomaticWinnerAndReload()
    {
        Planner.RemoveSlot(1);
        Set("_custName", "One activity");
        var handler = new EventHttpHandler();
        var service = new MW_GC.EventManager.Web.Services.EventService(new HttpClient(handler) { BaseAddress = new Uri("https://example.test") });
        page.SetService("EventSvc", service);
        await (Task)page.Call("SaveCustomizedEvent")!;
        Assert.AreEqual(HttpMethod.Post, handler.LastWrite);
        Assert.AreEqual(current.Id, handler.Saved!.WinnerActivityId);
        Assert.AreEqual(current.Id, Assert.ContainsSingle((List<EventEntity>)Get("_events")!).WinnerActivityId);

        page.Call("ShowEditEvent", handler.Saved);
        Assert.ContainsSingle(Slots.Cast<object>());
        Set("_custGameIds", new List<Guid> { gameA });
        Assert.AreEqual(replacement.Id, Assert.ContainsSingle(Candidates()).Id);
        Planner.RerollSlot(0);
        var replacementId = Id(Slots[0]!, "ActivityId");
        await (Task)page.Call("SaveCustomizedEvent")!;
        Assert.AreEqual(HttpMethod.Put, handler.LastWrite);
        Assert.AreEqual(replacementId, handler.Saved!.WinnerActivityId);
        Assert.AreEqual("One activity", handler.Saved.Name);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MultiSelectionEditPreservesOnlyAnExistingWinner(bool replaceWinner)
    {
        var handler = ConfigureSave();
        await Save();
        Assert.IsNull(handler.Saved!.WinnerActivityId);
        handler.Saved.WinnerActivityId = current.Id;
        page.Call("ShowEditEvent", handler.Saved);
        if (replaceWinner) Planner.RerollSlot(0);
        await Save();
        Assert.AreEqual(HttpMethod.Put, handler.LastWrite);
        Assert.AreEqual(2, handler.Saved!.Selections.Count);
        Assert.AreEqual(replaceWinner ? (Guid?)null : current.Id, handler.Saved.WinnerActivityId);
    }

    [TestMethod]
    public async Task SavedSoleWinnerIsReadBackForListAndDetails()
    {
        Planner.RemoveSlot(1);
        var handler = ConfigureSave();
        await Save();
        var persisted = Assert.ContainsSingle((List<EventEntity>)Get("_events")!);
        Assert.AreNotSame(handler.Saved, persisted);
        page.Call("ShowDetails", persisted);
        var detail = (EventEntity)Get("_detailEvent")!;
        Assert.AreEqual(Assert.ContainsSingle(detail.Selections).Activity.Id, detail.WinnerActivityId);
        Assert.AreSame(persisted, detail);
        Assert.AreEqual(1, handler.Reads);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task MultiSlotEditReducedToOneReloadsAutomaticWinner(bool hadWinner)
    {
        var handler = ConfigureSave();
        await Save();
        Assert.IsNotNull(handler.Saved);
        handler.Saved.WinnerActivityId = hadWinner ? other.Id : null;
        page.Call("ShowEditEvent", handler.Saved);
        Planner.RemoveSlot(1);
        await Save();
        Assert.IsNull(Get("_customizeError"));
        Assert.AreEqual(HttpMethod.Put, handler.LastWrite);
        Assert.AreEqual(current.Id, Assert.ContainsSingle(handler.Saved.Selections).Activity.Id);
        Assert.AreEqual(current.Id, handler.Saved.WinnerActivityId);
        var listed = Assert.ContainsSingle((List<EventEntity>)Get("_events")!);
        page.Call("ShowDetails", listed);
        Assert.AreEqual(current.Id, ((EventEntity)Get("_detailEvent")!).WinnerActivityId);
    }
}
