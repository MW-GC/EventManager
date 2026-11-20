using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Pages;

namespace MW_GC.EventManager.Web.Tests;

// #132: a Comment written from an Event's details lands on the Activity that won that Event and records
// the Event as its origin. The details dialog shows the Comments panel of the winning Activity only, read
// live from the Activity's own Comments route every time it opens, never from the Event's Selection
// snapshot. A Comment that names an Event shows "from {Event name}" in both dialogs; an Event that is gone
// shows "from a deleted Event", and when the Events list cannot be read, "from an Event".
[TestClass]
public sealed class CommentsFromEventDetailsTests : IDisposable
{
    // Azure Table Storage tags are weak and carry quotes; they must reach If-Match as read.
    private const string TagA = "W/\"datetime'2026-11-20T10%3A00%3A00.0000000Z'\"";
    private const string TagA2 = "W/\"datetime'2026-11-20T10%3A30%3A00.0000000Z'\"";
    private const string TagB = "W/\"datetime'2026-11-20T11%3A00%3A00.0000000Z'\"";
    private const string TagC = "W/\"datetime'2026-11-20T12%3A00%3A00.0000000Z'\"";
    // Text the Event's Selection snapshot and the Activity's old single field still carry (#133 removes
    // both); neither is ever shown as a Comment.
    private const string SnapshotText = "Snapshot text";
    private const string OldFieldText = "Old single text";

    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly GameEntity kart = new() { Name = "Kart" };
    private readonly ActivityEntity grandPrix;
    private readonly ActivityEntity timeTrial;
    // Won by Grand Prix.
    private readonly EventEntity night;
    // Two Selections and no Winner yet.
    private readonly EventEntity open;
    // Noon UTC, so the local date is the same day in every time zone the tests may run in.
    private static readonly DateTimeOffset Added = new(2025, 3, 14, 12, 0, 0, TimeSpan.Zero);

    // What the stub API stores per Activity: the Comments rows, oldest first.
    private readonly Dictionary<Guid, List<Stored>> comments = [];

    private sealed record Stored(Guid Id, string Text, Guid? EventId, string Tag);

    public CommentsFromEventDetailsTests()
    {
        grandPrix = new ActivityEntity { Name = "Grand Prix", GameId = kart.Id, Comments = OldFieldText };
        timeTrial = new ActivityEntity { Name = "Time Trial", GameId = kart.Id };
        night = new EventEntity
        {
            Name = "Game night",
            Date = new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero),
            UniqueGamesOnly = false,
            Selections = [Pick(grandPrix, SnapshotText), Pick(timeTrial)],
            WinnerActivityId = grandPrix.Id
        };
        open = new EventEntity
        {
            Name = "Open night",
            Date = new DateTimeOffset(2026, 10, 16, 19, 0, 0, TimeSpan.Zero),
            UniqueGamesOnly = false,
            Selections = [Pick(grandPrix, SnapshotText), Pick(timeTrial)]
        };
        api.Json(HttpMethod.Get, "/api/games", new[] { kart })
           .Json(HttpMethod.Get, "/api/activities", new[] { grandPrix, timeTrial })
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>())
           .Json(HttpMethod.Get, "/api/events", new[] { night, open })
           // The Activity details dialog's Performance section (#137) reads these too; answered so it loads quietly.
           .Json(HttpMethod.Get, "/api/event-metrics", Array.Empty<object>());
        foreach (var activity in new[] { grandPrix, timeTrial })
        {
            var id = activity.Id;
            comments[id] = [];
            api.On(HttpMethod.Get, ListPath(id), () => ListResponse(id));
        }
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    private Selection Pick(ActivityEntity act, string? snapshotComments = null) => new()
    {
        Game = new Game { Id = kart.Id, Name = kart.Name },
        Activity = new Activity { Id = act.Id, GameId = act.GameId, Name = act.Name, Comments = snapshotComments }
    };

    // ---- Where the entry shows -----------------------------------------------------------------

    [TestMethod]
    public async Task WinnerWithNoComments_ShowsThePanel_AndNoCommentsYet()
    {
        var page = await OpenEventDetails("Game night");

        var panel = Assert.ContainsSingle(Panels(page));
        Assert.AreSame(Card(page, "Grand Prix"), panel.Closest("fluent-card"));
        Assert.Contains("No Comments yet.", panel.TextContent);
        Assert.IsNotNull(NewBox(page));
        Assert.AreEqual(1, api.Count(HttpMethod.Get, ListPath(grandPrix.Id)));
        Assert.AreEqual(0, api.Count(HttpMethod.Get, ListPath(timeTrial.Id)));
        // The old single-text editor is gone, and neither the snapshot's nor the old field's text shows.
        var dialog = page.FormDialog();
        Assert.DoesNotContain("Event Manager Comments", dialog.TextContent);
        Assert.IsEmpty(dialog.QuerySelectorAll("fluent-button").Where(b => b.TextContent.Trim() == "Save Comments").ToList());
        Assert.DoesNotContain(SnapshotText, dialog.TextContent);
        Assert.DoesNotContain(OldFieldText, dialog.TextContent);
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

    [TestMethod]
    public async Task SelectWinner_OnANoWinnerEvent_ShowsTheEntry_AndLoadsThatActivitysComments()
    {
        comments[timeTrial.Id].Add(new Stored(Guid.NewGuid(), "Crowd favourite", null, TagB));
        var page = await OpenEventDetails("Open night");
        var won = Rewon(open, timeTrial);
        api.Json(HttpMethod.Put, $"/api/events/{open.Id}", won)
           .Json(HttpMethod.Get, "/api/events", new[] { night, won });

        await Card(page, "Time Trial").Button("Select Winner").ClickAsync(new());

        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { "Crowd favourite" }, Texts(page)));
        var panel = Assert.ContainsSingle(Panels(page));
        Assert.AreSame(Card(page, "Time Trial"), panel.Closest("fluent-card"));
        Assert.AreEqual(1, api.Count(HttpMethod.Get, ListPath(timeTrial.Id)));
        Assert.AreEqual(0, api.Count(HttpMethod.Get, ListPath(grandPrix.Id)));
    }

    [TestMethod]
    public async Task ChangingTheWinner_SwapsTheComments()
    {
        comments[grandPrix.Id].Add(new Stored(Guid.NewGuid(), "Ran long", null, TagA));
        comments[timeTrial.Id].Add(new Stored(Guid.NewGuid(), "Crowd favourite", null, TagB));
        var page = await OpenEventDetails("Game night");
        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { "Ran long" }, Texts(page)));
        var rewon = Rewon(night, timeTrial);
        api.Json(HttpMethod.Put, $"/api/events/{night.Id}", rewon)
           .Json(HttpMethod.Get, "/api/events", new[] { rewon, open });

        await Card(page, "Time Trial").Button("Select Winner").ClickAsync(new());

        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { "Crowd favourite" }, Texts(page)));
        var panel = Assert.ContainsSingle(Panels(page));
        Assert.AreSame(Card(page, "Time Trial"), panel.Closest("fluent-card"));
        Assert.DoesNotContain("Ran long", page.FormDialog().TextContent);
        Assert.AreEqual(1, api.Count(HttpMethod.Get, ListPath(timeTrial.Id)));
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

    // ---- Origin --------------------------------------------------------------------------------

    [TestMethod]
    public async Task ACommentWhoseEventWasDeleted_ShowsFromADeletedEvent_AndStaysEditable()
    {
        var gone = new Stored(Guid.NewGuid(), "From a night since deleted", Guid.NewGuid(), TagA);
        comments[grandPrix.Id].AddRange([
            gone,
            new Stored(Guid.NewGuid(), "From game night", night.Id, TagB),
            new Stored(Guid.NewGuid(), "Written on the Activities page", null, TagC)]);
        api.On(HttpMethod.Put, OnePath(grandPrix.Id, gone.Id), () => One(grandPrix.Id, gone with { Text = "Edited", Tag = TagA2 }));
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
        comments[grandPrix.Id].Add(new Stored(Guid.NewGuid(), "Ran long", night.Id, TagA));
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
        comments[grandPrix.Id].Add(new Stored(Guid.NewGuid(), "Written on the Activities page", null, TagA));
        var page = await OpenEventDetails("Game night");
        page.WaitForAssertion(() => Assert.HasCount(1, Texts(page)));
        Assert.IsEmpty(Origins(page));
        Assert.AreEqual(1, api.Count(HttpMethod.Get, "/api/events"));
        await page.FormDialog().Button("Close").ClickAsync(new());

        comments[grandPrix.Id].Add(new Stored(Guid.NewGuid(), "From game night", night.Id, TagB));
        comments[grandPrix.Id].Add(new Stored(Guid.NewGuid(), "From open night", open.Id, TagC));
        await EventDetailsButton(page, "Game night").ClickAsync(new());

        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { "from Game night", "from Open night" }, Origins(page)));
        Assert.AreEqual(2, api.Count(HttpMethod.Get, "/api/events"));
    }

    // ---- Live, not a snapshot ------------------------------------------------------------------

    [TestMethod]
    public async Task ASecondOpen_ReadsTheCommentsAgain_NeverTheSnapshot()
    {
        comments[grandPrix.Id].Add(new Stored(Guid.NewGuid(), "Ran long", null, TagA));
        var page = await OpenEventDetails("Game night");
        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { "Ran long" }, Texts(page)));
        await page.FormDialog().Button("Close").ClickAsync(new());

        // Someone adds a Comment on the Activities page while the dialog is closed.
        comments[grandPrix.Id].Add(new Stored(Guid.NewGuid(), "Crowd favourite", null, TagB));
        await EventDetailsButton(page, "Game night").ClickAsync(new());

        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { "Ran long", "Crowd favourite" }, Texts(page)));
        Assert.AreEqual(2, api.Count(HttpMethod.Get, ListPath(grandPrix.Id)));
        Assert.DoesNotContain(SnapshotText, page.FormDialog().TextContent);
        Assert.DoesNotContain(OldFieldText, page.FormDialog().TextContent);
    }

    // ---- The dialog's guards -------------------------------------------------------------------

    [TestMethod]
    [DataRow("add")]
    [DataRow("edit")]
    public async Task ADirtyAddBoxOrEdit_AsksToDiscard(string kind)
    {
        comments[grandPrix.Id].Add(new Stored(Guid.NewGuid(), "Ran long", null, TagA));
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
        var gate = api.Hold(HttpMethod.Post, path, () => One(grandPrix.Id, new Stored(Guid.NewGuid(), "New", night.Id, TagC), HttpStatusCode.Created));
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
        var first = new Stored(Guid.NewGuid(), "Ran long", night.Id, TagA);
        comments[grandPrix.Id].AddRange([first, new Stored(Guid.NewGuid(), "Crowd favourite", null, TagB)]);
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

    private static string ListPath(Guid activityId) => $"/api/activities/{activityId}/comments";

    private static string OnePath(Guid activityId, Guid commentId) => $"{ListPath(activityId)}/{commentId}";

    private static EventEntity Rewon(EventEntity e, ActivityEntity winner) => new()
    {
        Id = e.Id, Name = e.Name, Date = e.Date, UniqueGamesOnly = e.UniqueGamesOnly, Selections = e.Selections, WinnerActivityId = winner.Id
    };

    // The API's JSON for one Comment, with its eTag in the body as the API sends it.
    private static object Json(Guid activityId, Stored c) =>
        new { id = c.Id, activityId, text = c.Text, createdUtc = Added, eventId = c.EventId, eTag = c.Tag };

    private HttpResponseMessage ListResponse(Guid activityId) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(comments[activityId].Select(c => Json(activityId, c)).ToArray()) };

    private static HttpResponseMessage One(Guid activityId, Stored c, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = JsonContent.Create(Json(activityId, c)) };
        response.Headers.TryAddWithoutValidation("ETag", c.Tag);
        return response;
    }

    // An add, stored as the API would store it: the text and the Event id the request sent.
    private HttpResponseMessage StoreAdd(Guid activityId)
    {
        using var body = JsonDocument.Parse(api.Bodies(HttpMethod.Post, ListPath(activityId)).Last()!);
        Guid? eventId = body.RootElement.TryGetProperty("eventId", out var id) ? id.GetGuid() : null;
        var stored = new Stored(Guid.NewGuid(), body.RootElement.GetProperty("text").GetString()!, eventId, TagC);
        comments[activityId].Add(stored);
        return One(activityId, stored, HttpStatusCode.Created);
    }

    private async Task<IRenderedComponent<Events>> OpenEventDetails(string name)
    {
        api.On(HttpMethod.Post, ListPath(grandPrix.Id), () => StoreAdd(grandPrix.Id));
        var page = harness.Render<Events>();
        page.WaitForRows(2);
        await EventDetailsButton(page, name).ClickAsync(new());
        Assert.IsTrue(page.HasFormDialog());
        if (name == night.Name) page.WaitForAssertion(() => Assert.IsNotNull(NewBox(page)));
        return page;
    }

    private IRenderedComponent<Activities> OpenActivities()
    {
        var page = harness.Render<Activities>();
        page.WaitForRows(2);
        page.WaitForAssertion(() => Assert.IsFalse(page.Rows().Any(r => r.Contains('—'))));
        return page;
    }

    private static IElement RowButton<T>(IRenderedComponent<T> page, string item, string title) where T : IComponent =>
        page.FindAll("tbody tr.fluent-data-grid-row")
            .Where(r => r.Closest("fluent-dialog") is null)
            .Single(r => r.TextContent.Contains(item))
            .QuerySelector($"fluent-button[title={title}]")!;

    private static IElement EventDetailsButton(IRenderedComponent<Events> page, string name) => RowButton(page, name, "Details");

    private static IElement ActivityDetailsButton(IRenderedComponent<Activities> page, string name) => RowButton(page, name, "Details");

    // The Selection card of one Activity in the Event details dialog.
    private static IElement Card(IRenderedComponent<Events> page, string activity) =>
        page.FormDialog().QuerySelectorAll("fluent-card")
            .Single(c => c.QuerySelectorAll(".fluent-typography").Any(l => l.TextContent.Trim() == activity));

    private static List<IElement> Panels<T>(IRenderedComponent<T> page) where T : IComponent =>
        page.FormDialog().QuerySelectorAll(".activity-comments").ToList();

    private static List<IElement> Items<T>(IRenderedComponent<T> page) where T : IComponent =>
        page.FormDialog().QuerySelectorAll("li.comment").ToList();

    private static List<string> Texts<T>(IRenderedComponent<T> page) where T : IComponent =>
        Items(page).Select(i => i.QuerySelector(".comment-text")?.TextContent ?? "(editing)").ToList();

    // The origin beside a Comment's date, or null for a Comment that names no Event.
    private static string? Origin(IElement item) => item.QuerySelector(".comment-origin")?.TextContent.Trim();

    private static List<string> Origins<T>(IRenderedComponent<T> page) where T : IComponent =>
        Items(page).Select(Origin).OfType<string>().ToList();

    private static IElement? NewBox<T>(IRenderedComponent<T> page) where T : IComponent =>
        page.FormDialog().QuerySelector("fluent-text-area[aria-label='New Comment']");

    private static IElement? EditBox<T>(IRenderedComponent<T> page) where T : IComponent =>
        page.FormDialog().QuerySelector("fluent-text-area[aria-label^='Edit Comment from']");

    private static Task TypeNew<T>(IRenderedComponent<T> page, string text) where T : IComponent =>
        NewBox(page)!.InputAsync(new ChangeEventArgs { Value = text });

    // A click whose request finishes at once; waits for the render after it.
    private static async Task Click<T>(IElement button, IRenderedComponent<T> page) where T : IComponent
    {
        await button.ClickAsync(new());
        page.WaitForAssertion(() => Assert.IsFalse(page.FormDialog().Button("Close").IsBusy()));
    }

    // Overlay click or Escape on the details dialog.
    private static Task Dismiss<T>(IRenderedComponent<T> page) where T : IComponent
    {
        var dialog = page.FormDialog();
        return dialog.TriggerEventAsync("ondialogdismiss", new DialogEventArgs { Id = dialog.Id, Reason = "dismiss" });
    }
}
