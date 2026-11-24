using System.Net;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using static MW_GC.EventManager.Web.Tests.CommentsPanelView;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// #132: a Comment written from an Event's details lands on the Activity that won that Event and records
// the Event as its origin. The details dialog shows the Comments panel of the winning Activity only, read
// live from the Activity's own Comments route every time it opens, never from the Event's Selection
// snapshot. A Comment that names an Event shows "from {Event name}" in both dialogs; an Event that is gone
// shows "from a deleted Event", and when the Events list cannot be read, "from an Event". Select Winner and
// the panel are in CommentsSelectWinnerTests.
[TestClass]
public sealed class CommentsFromEventDetailsTests : CommentsFromEventDetailsFixture
{
    // ---- Where the entry shows -----------------------------------------------------------------

    [TestMethod]
    public async Task WinnerWithNoComments_ShowsThePanel_AndNoCommentsYet()
    {
        ServeOldSnapshots();
        var page = await OpenEventDetails("Game night");

        var panel = Assert.ContainsSingle(Panels(page));
        Assert.AreSame(Card(page, "Grand Prix"), panel.Closest("fluent-card"));
        Assert.Contains("No Comments yet.", panel.TextContent);
        Assert.IsNotNull(NewBox(page));
        Assert.AreEqual(1, api.Count(HttpMethod.Get, ListPath(grandPrix.Id)));
        Assert.AreEqual(0, api.Count(HttpMethod.Get, ListPath(timeTrial.Id)));
        // The editor of the old text is gone, and the old text an Event stored in its snapshot shows nowhere:
        // not as text and not as the value of a text box.
        var dialog = page.FormDialog();
        Assert.DoesNotContain("Event Manager Comments", dialog.TextContent);
        Assert.IsEmpty(dialog.QuerySelectorAll("fluent-button").Where(b => b.TextContent.Trim() == "Save Comments").ToList());
        AssertNotShown(dialog, OldSnapshotText);
    }

    [TestMethod]
    public async Task AnEventWithNoWinner_ShowsNoCommentEntry()
    {
        var page = await OpenEventDetails("Open night");

        Assert.IsEmpty(Panels(page));
        Assert.IsNull(NewBox(page));
        Assert.IsEmpty(page.FormDialog().QuerySelectorAll("fluent-text-area").ToList());
        Assert.DoesNotContain("No Comments yet.", page.FormDialog().TextContent);
        Assert.IsEmpty(api.Requests.Where(r => r.Path.EndsWith("/comments")).ToList());
    }

    // ---- Add from Event details ----------------------------------------------------------------

    [TestMethod]
    public async Task Add_SendsTheTextAndTheEventId_AndShowsFromTheEventName()
    {
        var page = await OpenEventDetails("Game night");
        var eventReads = api.Count(HttpMethod.Get, "/api/events");

        await TypeNew(page, "  Cut the second round \n ");
        await Click(page.FormDialog().Button("Add Comment"), page);

        Assert.AreEqual($"{{\"text\":\"Cut the second round\",\"eventId\":\"{night.Id}\"}}",
            Assert.ContainsSingle(api.Bodies(HttpMethod.Post, ListPath(grandPrix.Id))));
        CollectionAssert.AreEqual(new[] { "Cut the second round" }, Texts(page));
        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { "from Game night" }, Origins(page)));
        Assert.IsEmpty(page.FormDialog().Alerts());
        Assert.AreEqual(0, api.Count(HttpMethod.Post, ListPath(timeTrial.Id)));

        // The Event names are read once per panel: a second add from here reads them no more.
        Assert.AreEqual(eventReads + 1, api.Count(HttpMethod.Get, "/api/events"));
        await TypeNew(page, "Start earlier");
        await Click(page.FormDialog().Button("Add Comment"), page);
        CollectionAssert.AreEqual(new[] { "from Game night", "from Game night" }, Origins(page));
        Assert.AreEqual(eventReads + 1, api.Count(HttpMethod.Get, "/api/events"));
    }

    [TestMethod]
    public async Task ACommentAddedHere_ShowsInAFreshlyOpenedActivityDetails_WithTheEventName()
    {
        var events = await OpenEventDetails("Game night");
        await TypeNew(events, "Ran long");
        await Click(events.FormDialog().Button("Add Comment"), events);
        await events.FormDialog().Button("Close").ClickAsync(new());

        var activities = OpenActivities();
        await ActivityDetailsButton(activities, "Grand Prix").ClickAsync(new());

        activities.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { "Ran long" }, Texts(activities)));
        activities.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { "from Game night" }, Origins(activities)));
        Assert.AreEqual(night.Id, Assert.ContainsSingle(comments[grandPrix.Id]).EventId);
    }

    // #186 item 1: a failed add from Event details keeps the typed text and says why; a retry then adds it,
    // still recording this Event.
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AFailedAdd_KeepsTheTextAndSaysWhy_AndARetrySucceeds(bool offline)
    {
        var page = await OpenEventDetails("Game night");
        var path = ListPath(grandPrix.Id);
        if (offline) api.Offline(HttpMethod.Post, path);
        else api.Status(HttpMethod.Post, path, HttpStatusCode.InternalServerError);

        await TypeNew(page, "Cut the second round");
        await Click(page.FormDialog().Button("Add Comment"), page);

        var why = offline
            ? "Could not reach the server. Check the connection and try again."
            : "The server had a problem (HTTP 500). Try again.";
        Assert.AreEqual($"The Comment was not added. {why}", Assert.ContainsSingle(page.FormDialog().Alerts()));
        Assert.AreEqual("Cut the second round", NewBox(page)!.GetAttribute("value"));
        Assert.IsEmpty(Items(page));
        Assert.IsTrue(page.HasFormDialog());

        api.On(HttpMethod.Post, path, () => StoreAdd(grandPrix.Id));
        await Click(page.FormDialog().Button("Add Comment"), page);

        CollectionAssert.AreEqual(new[] { "Cut the second round" }, Texts(page));
        Assert.IsEmpty(page.FormDialog().Alerts());
        Assert.AreEqual("", NewBox(page)!.GetAttribute("value") ?? "");
        var expected = $"{{\"text\":\"Cut the second round\",\"eventId\":\"{night.Id}\"}}";
        CollectionAssert.AreEqual(new[] { expected, expected }, api.Bodies(HttpMethod.Post, path));
        Assert.AreEqual(night.Id, Assert.ContainsSingle(comments[grandPrix.Id]).EventId);
        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { "from Game night" }, Origins(page)));
    }

    // ---- Origin --------------------------------------------------------------------------------

    [TestMethod]
    public async Task ACommentWhoseEventWasDeleted_ShowsFromADeletedEvent_AndStaysEditable()
    {
        var gone = Comment("From a night since deleted", Guid.NewGuid(), TagA);
        comments[grandPrix.Id].AddRange([
            gone,
            Comment("From game night", night.Id, TagB),
            Comment("Written on the Activities page", null, TagC)]);
        api.On(HttpMethod.Put, OnePath(grandPrix.Id, gone.Id), () => (gone with { Text = "Edited", Tag = TagA2 }).Response(grandPrix.Id));
        var page = await OpenEventDetails("Game night");

        page.WaitForAssertion(() => CollectionAssert.AreEqual(
            new[] { "from a deleted Event", "from Game night", null }, Items(page).Select(Origin).ToList()));

        await Click(Items(page)[0].Button("Edit"), page);
        await EditBox(page)!.InputAsync(new ChangeEventArgs { Value = "Edited" });
        await Click(page.FormDialog().Button("Save"), page);

        Assert.AreEqual(TagA, Assert.ContainsSingle(api.IfMatches(HttpMethod.Put, OnePath(grandPrix.Id, gone.Id))));
        Assert.AreEqual("Edited", Texts(page)[0]);
        Assert.AreEqual("from a deleted Event", Origin(Items(page)[0]));
        Assert.IsEmpty(page.FormDialog().Alerts());
    }

    [TestMethod]
    public async Task TheEventsListFailing_ShowsFromAnEvent_WithNoError()
    {
        comments[grandPrix.Id].Add(Comment("Ran long", night.Id, TagA));
        api.Status(HttpMethod.Get, "/api/events", HttpStatusCode.InternalServerError);
        var page = OpenActivities();

        await ActivityDetailsButton(page, "Grand Prix").ClickAsync(new());

        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { "Ran long" }, Texts(page)));
        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { "from an Event" }, Origins(page)));
        Assert.DoesNotContain("deleted", page.FormDialog().TextContent);
        Assert.IsEmpty(page.FormDialog().Alerts());
        Assert.IsEmpty(page.PageAlerts());
    }

    // Through the Events page, whose details dialog reads nothing else from api/events: the page's own list
    // read is the only other one.
    [TestMethod]
    public async Task TheEventNamesAreReadOnce_AndOnlyWhenACommentHasAnEvent()
    {
        comments[grandPrix.Id].Add(Comment("Written on the Activities page", null, TagA));
        var page = await OpenEventDetails("Game night");
        page.WaitForAssertion(() => Assert.HasCount(1, Texts(page)));
        Assert.IsEmpty(Origins(page));
        Assert.AreEqual(1, api.Count(HttpMethod.Get, "/api/events"));
        await page.FormDialog().Button("Close").ClickAsync(new());

        comments[grandPrix.Id].Add(Comment("From game night", night.Id, TagB));
        comments[grandPrix.Id].Add(Comment("From open night", open.Id, TagC));
        await EventDetailsButton(page, "Game night").ClickAsync(new());

        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { "from Game night", "from Open night" }, Origins(page)));
        Assert.AreEqual(2, api.Count(HttpMethod.Get, "/api/events"));
    }

    // ---- Live, not a snapshot ------------------------------------------------------------------

    [TestMethod]
    public async Task ASecondOpen_ReadsTheCommentsAgain_NeverTheSnapshot()
    {
        ServeOldSnapshots();
        comments[grandPrix.Id].Add(Comment("Ran long", null, TagA));
        var page = await OpenEventDetails("Game night");
        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { "Ran long" }, Texts(page)));
        await page.FormDialog().Button("Close").ClickAsync(new());

        // Someone adds a Comment on the Activities page while the dialog is closed.
        comments[grandPrix.Id].Add(Comment("Crowd favourite", null, TagB));
        await EventDetailsButton(page, "Game night").ClickAsync(new());

        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { "Ran long", "Crowd favourite" }, Texts(page)));
        Assert.AreEqual(2, api.Count(HttpMethod.Get, ListPath(grandPrix.Id)));
        AssertNotShown(page.FormDialog(), OldSnapshotText);
    }

    // ---- The dialog's guards -------------------------------------------------------------------

    [TestMethod]
    [DataRow("add")]
    [DataRow("edit")]
    public async Task ADirtyAddBoxOrEdit_AsksToDiscard(string kind)
    {
        comments[grandPrix.Id].Add(Comment("Ran long", null, TagA));
        var page = await OpenEventDetails("Game night");
        page.WaitForAssertion(() => Assert.HasCount(1, Items(page)));
        Func<string?> typed;
        if (kind == "add")
        {
            await TypeNew(page, "Half a thought");
            typed = () => NewBox(page)!.GetAttribute("value");
        }
        else
        {
            await Click(Items(page)[0].Button("Edit"), page);
            await EditBox(page)!.InputAsync(new ChangeEventArgs { Value = "Ran longer" });
            typed = () => EditBox(page)!.GetAttribute("value");
        }

        await Dismiss(page);
        Assert.AreEqual("Discard changes?", page.Confirmation().QuerySelector("h4")!.TextContent.Trim());
        await page.Confirmation().Button("Keep editing").ClickAsync(new());
        Assert.IsTrue(page.HasFormDialog());
        Assert.AreEqual(kind == "add" ? "Half a thought" : "Ran longer", typed());

        await Dismiss(page);
        await page.ConfirmAsync("Discard");
        Assert.IsFalse(page.HasFormDialog());
        Assert.IsEmpty(api.Requests.Where(r => r.Method != HttpMethod.Get).ToList());
    }

    [TestMethod]
    public async Task WhileAnAddRuns_CloseAndEscapeDoNothing_AndASecondClickSendsNothing()
    {
        var page = await OpenEventDetails("Game night");
        var path = ListPath(grandPrix.Id);
        var gate = api.Hold(HttpMethod.Post, path, () => Comment("New", night.Id, TagC).Response(grandPrix.Id, HttpStatusCode.Created));
        await TypeNew(page, "New");

        var first = page.FormDialog().Button("Add Comment").ClickAsync(new());
        // The click may still be queued behind other renderer work, so wait for its render.
        page.WaitForAssertion(() =>
        {
            Assert.IsTrue(page.FormDialog().Button("Add Comment").IsBusy(), "the Add Comment button");
            Assert.IsTrue(page.FormDialog().Button("Close").IsBusy(), "the Close button");
        });
        await page.FormDialog().Button("Add Comment").ClickAsync(new());
        await Dismiss(page);
        await page.FormDialog().Button("Close").ClickAsync(new());
        Assert.IsTrue(page.HasFormDialog());
        Assert.IsFalse(page.HasConfirmation());

        gate.SetResult();
        await first;

        page.WaitForAssertion(() => Assert.IsFalse(page.FormDialog().Button("Close").IsBusy()));
        Assert.AreEqual(1, api.Count(HttpMethod.Post, path));
        CollectionAssert.AreEqual(new[] { "New" }, Texts(page));
        Assert.IsEmpty(page.FormDialog().Alerts());
    }

    [TestMethod]
    public async Task DeleteAsksInTheDialogsQuestion_AndKeepsTheDialogOpen()
    {
        var first = Comment("Ran long", night.Id, TagA);
        comments[grandPrix.Id].AddRange([first, Comment("Crowd favourite", null, TagB)]);
        var path = OnePath(grandPrix.Id, first.Id);
        api.Status(HttpMethod.Delete, path, HttpStatusCode.NoContent);
        var page = await OpenEventDetails("Game night");
        page.WaitForAssertion(() => Assert.HasCount(2, Items(page)));

        await Click(Items(page)[0].Button("Delete"), page);
        Assert.AreEqual("Delete Comment", page.Confirmation().QuerySelector("h4")!.TextContent.Trim());
        await page.Confirmation().Button("Cancel").ClickAsync(new());
        Assert.IsTrue(page.HasFormDialog());
        Assert.AreEqual(0, api.Count(HttpMethod.Delete, path));

        await Click(Items(page)[0].Button("Delete"), page);
        await page.ConfirmAsync();
        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { "Crowd favourite" }, Texts(page)));

        Assert.AreEqual(TagA, Assert.ContainsSingle(api.IfMatches(HttpMethod.Delete, path)));
        Assert.IsTrue(page.HasFormDialog());
        // The question was over the open dialog: focus is not sent back to the row's Details button.
        harness.JSInterop.VerifyNotInvoke("emFocus.restore");
    }

    // ---- Helpers -------------------------------------------------------------------------------

    // The old text an Event stored before #133 in its Selection snapshot, under the retired "comments" name.
    private const string OldSnapshotText = "Old text from the snapshot";

    // Answers the Events list in the shape stored before #133: every Selection's Activity snapshot still carries
    // the old text. The page must load it and never show it.
    private void ServeOldSnapshots() => api.On(HttpMethod.Get, "/api/events", () =>
    {
        var list = JsonSerializer.SerializeToNode(new[] { night, open }, JsonSerializerOptions.Web)!.AsArray();
        foreach (var ev in list)
        foreach (var selection in ev!["selections"]!.AsArray())
            selection!["activity"]!["comments"] = OldSnapshotText;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(list.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
    });

    // The text is neither shown nor the value of any input, text area or Fluent text field inside root.
    private static void AssertNotShown(IElement root, string text)
    {
        Assert.DoesNotContain(text, root.TextContent);
        foreach (var box in root.QuerySelectorAll("input, textarea, fluent-text-field, fluent-text-area"))
        {
            Assert.DoesNotContain(text, box.GetAttribute("value") ?? "", box.OuterHtml);
            Assert.DoesNotContain(text, box.GetAttribute("current-value") ?? "", box.OuterHtml);
        }
    }
}
