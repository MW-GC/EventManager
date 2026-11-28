using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Web.Tests;

// A save that fails, or whose answer is lost: the dialog keeps its selections and error for a retry, and an
// ambiguous create retries under the same Idempotency-Key. Fixture: SlotPageFixture.
[TestClass]
public class SlotSaveFailureTests : SlotPageFixture
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedSaveKeepsGenerationErrorAndSelections(bool editing)
    {
        const string error = "Previous generation failure";
        Planner.GenerationError = error;
        Set("_custName", "Keep this name");
        Set("_showCustomize", true);
        if (editing) Set("_editingEvent", new EventEntity { Id = Guid.NewGuid() });
        using var http = new HttpClient(new FailedSaveHandler()) { BaseAddress = new Uri("https://example.invalid/") };
        page.SetService("EventSvc", new MW_GC.EventManager.Web.Services.EventService(http));
        var original = Slots;
        var originalSlots = Slots.Cast<object>().ToArray();

        await (Task)Call("SaveCustomizedEvent")!;

        Assert.IsNotNull(Get("_customizeError"));
        Assert.AreEqual(error, Planner.GenerationError);
        Assert.AreSame(original, Slots);
        Assert.AreSequenceEqual(originalSlots, Slots.Cast<object>().ToArray());
        Assert.AreEqual("Keep this name", Get("_custName"));
        Assert.AreEqual(true, Get("_showCustomize"));
        Assert.AreEqual(false, Get("_generating"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SaveFailureKeepsDialogAndSelectionsForRetry(bool networkFailure)
    {
        Planner.RemoveSlot(1);
        var handler = ConfigureSave();
        handler.Fail = true;
        handler.NetworkFailure = networkFailure;
        await Save();
        Assert.AreEqual(true, Get("_showCustomize"));
        Assert.AreEqual(false, Get("_generating"));
        Assert.IsNotNull(Get("_customizeError"));
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(current.Id, Id(Slots[0]!, "ActivityId"));
        handler.Fail = false;
        handler.NetworkFailure = false;
        await Save();
        Assert.IsNull(Get("_customizeError"));
        Assert.AreEqual(false, Get("_showCustomize"));
        Assert.AreEqual(1, handler.Reads);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("{")]
    [DataRow("[{\"id\":\"not-a-guid\"}]")]
    public async Task SuccessfulSaveWithFailedReadBackDoesNotOfferCreateRetry(string? readJson)
    {
        Planner.RemoveSlot(1);
        var handler = ConfigureSave();
        handler.FailRead = readJson is null;
        handler.ReadJson = readJson;
        await Save();
        Assert.AreEqual(current.Id, handler.Saved!.WinnerActivityId);
        Assert.AreEqual(false, Get("_showCustomize"));
        Assert.IsNull(Get("_customizeError"));
        Assert.Contains("event was saved", (string)Get("_pageError")!);
        Assert.AreEqual(false, Get("_generating"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AmbiguousCreateRetryReusesKeyAndNewDialogGetsNewKey(bool timeout)
    {
        Planner.RemoveSlot(1);
        var handler = ConfigureSave();
        handler.LoseWriteResponse = true;
        handler.Timeout = timeout;
        await Save();
        Assert.AreEqual(true, Get("_showCustomize"));
        Assert.IsNotNull(Get("_customizeError"));
        var key = Assert.ContainsSingle(handler.CreateKeys);
        Assert.IsTrue(Guid.TryParse(key, out var id));
        Assert.AreNotEqual(Guid.Empty, id);
        var committedId = handler.Saved!.Id;

        handler.LoseWriteResponse = false;
        await Save();
        Assert.AreSequenceEqual(new[] { key, key }, handler.CreateKeys);
        Assert.AreEqual(committedId, handler.Saved.Id);
        Assert.ContainsSingle(handler.Created);
        Assert.AreEqual(false, Get("_showCustomize"));
        Assert.IsNull(Get("_customizeError"));
        Assert.AreEqual(committedId, Assert.ContainsSingle((List<EventEntity>)Get("_events")!).Id);

        page.Call("ShowCustomize");
        // ShowCustomize fills as many slots as the two-game fixture allows (here two);
        // pin the single-slot state this test saves instead of relying on that draw.
        SetSlots(current);
        Set("_custName", "Another event");
        await Save();
        Assert.AreNotEqual(key, handler.CreateKeys[2]);
        Assert.AreEqual(2, handler.Created.Count);
    }

    private sealed class FailedSaveHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(new HttpRequestException("Save failed"));
    }
}
