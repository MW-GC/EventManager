using System.Net;
using System.Text;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using MW_GC.EventManager.Web.Shared.Events;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// The review of #144: the Events page hands its list, its library and its callbacks to EventList, EventEditDialog
// and EventDetailsDialog in Events.razor. UnrenderedEventsPage wires those parts by hand, so a mis-wired parameter
// in the page would not fail the tests built on it. These render the real page through the harness, against a
// stub API that stores the Events, and drive each hand-off as a user does: each fails when its parameter or
// callback in Events.razor is removed.
[TestClass]
public sealed class EventsPageWiringTests : IDisposable
{
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly GameEntity kart = new() { Name = "Kart", IconUrl = "https://example.test/kart.png" };
    private readonly GameEntity brawl = new() { Name = "Brawl" };
    private readonly ActivityEntity grandPrix;
    private readonly ActivityEntity stock;
    private readonly EventEntity night;
    private readonly string nightPath;
    // The Events as the stub API stores them: every write changes this list and every list read returns it.
    private readonly List<EventEntity> stored;

    public EventsPageWiringTests()
    {
        grandPrix = new ActivityEntity { Name = "Grand Prix", GameId = kart.Id };
        stock = new ActivityEntity { Name = "Stock", GameId = brawl.Id };
        night = new EventEntity
        {
            Name = "Game night",
            Date = new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero),
            Selections = [Pick(kart, grandPrix), Pick(brawl, stock)],
            WinnerActivityId = grandPrix.Id
        };
        nightPath = $"/api/events/{night.Id}";
        stored = [night];
        api.Json(HttpMethod.Get, "/api/games", new[] { kart, brawl })
           .Json(HttpMethod.Get, "/api/activities", new[] { grandPrix, stock })
           // Non-empty, and of different lengths, so a Theme list handed over as the Holidays (or none) shows.
           .Json(HttpMethod.Get, "/api/themes", new[] { new ThemeEntity { Name = "Spooky" }, new ThemeEntity { Name = "Cozy" } })
           .Json(HttpMethod.Get, "/api/holidays", new[] { new HolidayEntity { Name = "Halloween" } })
           .Json(HttpMethod.Get, $"/api/activities/{grandPrix.Id}/comments", Array.Empty<CommentEntity>())
           .Json(HttpMethod.Get, $"/api/activities/{stock.Id}/comments", Array.Empty<CommentEntity>())
           .Json(HttpMethod.Get, $"{nightPath}/metrics", new { id = night.Id })
           .On(HttpMethod.Get, "/api/events", () => Answer(HttpStatusCode.OK, stored))
           .On(HttpMethod.Put, nightPath, () =>
           {
               var saved = Sent(HttpMethod.Put, nightPath);
               stored[stored.FindIndex(e => e.Id == saved.Id)] = saved;
               return Answer(HttpStatusCode.OK, saved);
           })
           .On(HttpMethod.Delete, nightPath, () =>
           {
               stored.RemoveAll(e => e.Id == night.Id);
               return new HttpResponseMessage(HttpStatusCode.NoContent);
           })
           .On(HttpMethod.Post, "/api/events", () =>
           {
               var created = Sent(HttpMethod.Post, "/api/events");
               created.Id = Guid.NewGuid();
               stored.Add(created);
               return Answer(HttpStatusCode.Created, created);
           });
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    // EventList OnDetails -> OpenDetails, the details dialog's @ref and its Games.
    [TestMethod]
    public async Task ARowsDetails_OpensTheDetailsDialogOfThatEvent()
    {
        var page = Loaded();

        await RowButton(page, "Game night", "Details").ClickAsync(new());

        Assert.IsTrue(page.HasFormDialog(), "Details opened no dialog");
        Assert.AreEqual("Game night", page.FormDialog().GetAttribute("aria-label"));
        Assert.IsNotNull(Card(page, "Stock").QuerySelectorAll("fluent-button").SingleOrDefault(b => b.TextContent.Trim() == "Select Winner"));
        // The Game icon comes from the library the page hands the dialog.
        Assert.AreEqual(kart.IconUrl, Card(page, "Grand Prix").QuerySelector("img")?.GetAttribute("src"));
    }

    // EventList OnEdit -> OpenEdit, and the edit dialog's @ref.
    [TestMethod]
    public async Task ARowsEdit_OpensTheEditDialogOfThatEvent()
    {
        var page = Loaded();

        await RowButton(page, "Game night", "Edit").ClickAsync(new());

        Assert.IsTrue(page.HasFormDialog(), "Edit opened no dialog");
        Assert.AreEqual("Edit Event", page.DialogTitle());
        Assert.AreEqual("Game night", EventName(page).GetAttribute("value"));
    }

    // EventEditDialog Saved -> OnEventSaved: the page reads its list again and shows the saved Event.
    [TestMethod]
    public async Task AnEditSave_ShowsTheChangeInTheList()
    {
        var page = Loaded();
        await RowButton(page, "Game night", "Edit").ClickAsync(new());
        await EventName(page).ChangeAsync(new ChangeEventArgs { Value = "Game night renamed" });

        await page.FormDialog().Button("Update Event").ClickAsync(new());

        page.WaitForAssertion(() => Assert.Contains("Game night renamed", Assert.ContainsSingle(page.Rows())));
        Assert.IsFalse(page.HasFormDialog());
        Assert.AreEqual(1, api.Count(HttpMethod.Put, nightPath));
        Assert.AreEqual(2, api.Count(HttpMethod.Get, "/api/events"));
    }

    // EventList OnDelete -> AskDelete, the page's question, and the refresh after the delete.
    [TestMethod]
    public async Task Delete_AsksFirst_ThenRemovesTheEventFromTheList()
    {
        var page = Loaded();

        await RowButton(page, "Game night", "Delete").ClickAsync(new());

        Assert.IsTrue(page.HasConfirmation(), "Delete asked nothing");
        Assert.AreEqual("Delete event", page.Confirmation().QuerySelector("h4")!.TextContent.Trim());
        Assert.AreEqual(0, api.Count(HttpMethod.Delete, nightPath));
        await page.ConfirmAsync();

        page.WaitForAssertion(() => Assert.IsFalse(page.Rows().Any(r => r.Contains("Game night"))));
        Assert.AreEqual(1, api.Count(HttpMethod.Delete, nightPath));
        Assert.IsEmpty(page.PageAlerts());
    }

    // EventList OnCreate -> OpenCreate, the edit dialog's Games and Activities (its slots roll from them), and
    // Saved -> OnEventSaved.
    [TestMethod]
    public async Task Create_OpensTheDialog_AndTheSavedEventShowsInTheList()
    {
        var page = Loaded();

        await page.Button("Create Event").ClickAsync(new());

        Assert.IsTrue(page.HasFormDialog(), "Create Event opened no dialog");
        Assert.AreEqual("Customize Event", page.DialogTitle());
        await EventName(page).ChangeAsync(new ChangeEventArgs { Value = "Quiz night" });
        await page.FormDialog().Button("Create Event").ClickAsync(new());

        page.WaitForAssertion(() => Assert.IsTrue(page.Rows().Any(r => r.Contains("Quiz night")), "the new Event is not in the list"));
        Assert.IsFalse(page.HasFormDialog());
        var created = Sent(HttpMethod.Post, "/api/events");
        CollectionAssert.AreEquivalent(new[] { grandPrix.Id, stock.Id }, created.Selections.Select(s => s.Activity.Id).ToList());
        Assert.HasCount(2, page.Rows());
    }

    // EventDetailsDialog RefreshEvents -> RefreshEvents and FindEvent -> FindEvent: after Select Winner the page
    // reads its list again, the row shows the new Winner, and the dialog stays open on the Event as read.
    [TestMethod]
    public async Task SelectWinnerInDetails_UpdatesTheWinnerInTheListAndTheDialog()
    {
        var page = Loaded();
        await RowButton(page, "Game night", "Details").ClickAsync(new());
        var reads = api.Count(HttpMethod.Get, "/api/events");

        await Card(page, "Stock").Button("Select Winner").ClickAsync(new());

        page.WaitForAssertion(() => Assert.AreEqual(reads + 1, api.Count(HttpMethod.Get, "/api/events")));
        Assert.Contains("Brawl — Stock", Assert.ContainsSingle(page.Rows()));
        Assert.AreEqual(stock.Id, Assert.ContainsSingle(stored).WinnerActivityId);
        Assert.IsTrue(page.HasFormDialog(), "the details dialog closed after Select Winner");
        Assert.AreEqual("Stock", WinnerInDialog(page));
        Assert.IsEmpty(page.FormDialog().Alerts());
    }

    // EventEditDialog Themes and Holidays: each filter list shows the page's own list, never the other one.
    [TestMethod]
    public async Task TheCreateDialogFiltersOfferTheLibrarysThemesAndHolidays()
    {
        var page = Loaded();

        await page.Button("Create Event").ClickAsync(new());

        CollectionAssert.AreEqual(new[] { "Spooky", "Cozy" }, FilterOptions(page, "Filter by themes"));
        CollectionAssert.AreEqual(new[] { "Halloween" }, FilterOptions(page, "Filter by holidays"));
    }

    // EventEditDialog PageError: a save clears the page's message first. Here the message is the page's own, after a
    // saved Event whose list refresh failed; the next save clears it.
    [TestMethod]
    public async Task TheNextSave_ClearsThePagesMessage()
    {
        var page = Loaded();
        api.Offline(HttpMethod.Get, "/api/events");
        await RowButton(page, "Game night", "Edit").ClickAsync(new());
        await EventName(page).ChangeAsync(new ChangeEventArgs { Value = "Game night renamed" });
        await page.FormDialog().Button("Update Event").ClickAsync(new());
        page.WaitForAssertion(() => Assert.AreEqual(RefreshFailed, Assert.ContainsSingle(PlainPageAlerts(page))));

        // The list was not refreshed, so its row still shows the old name.
        api.On(HttpMethod.Get, "/api/events", () => Answer(HttpStatusCode.OK, stored));
        await RowButton(page, "Game night", "Edit").ClickAsync(new());
        await EventName(page).ChangeAsync(new ChangeEventArgs { Value = "Game night again" });
        await page.FormDialog().Button("Update Event").ClickAsync(new());

        page.WaitForAssertion(() => Assert.Contains("Game night again", Assert.ContainsSingle(page.Rows())));
        Assert.IsEmpty(page.PageAlerts());
    }

    // EventEditDialog ActionError: a save that fails after its dialog was closed shows in the page's dismissable
    // error bar. While a save runs no page event closes the dialog (Cancel and Escape wait), so the test closes it
    // through the dialog's own close method, as an EventCallback on the dialog (which renders it after), as
    // DialogSafetyTests does.
    [TestMethod]
    public async Task AnEditSaveFailingAfterItsDialogClosed_ShowsInThePagesErrorBar()
    {
        var page = Loaded();
        var gate = api.Hold(HttpMethod.Put, nightPath, () => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await RowButton(page, "Game night", "Edit").ClickAsync(new());
        await EventName(page).ChangeAsync(new ChangeEventArgs { Value = "Game night renamed" });
        var save = page.FormDialog().Button("Update Event").ClickAsync(new());
        page.WaitForAssertion(() => Assert.IsTrue(page.FormDialog().Button("Cancel").IsBusy()));
        var edit = page.FindComponent<EventEditDialog>().Instance;
        await page.InvokeAsync(() => EventCallback.Factory.Create(edit, edit.CloseCustomize).InvokeAsync());

        gate.SetResult();
        await save;

        AssertInErrorBar(page, "The event was not saved. The server had a problem (HTTP 500). Try again.");
    }

    // EventDetailsDialog ActionError: a Select Winner that fails after its dialog was closed shows in the page's
    // error bar. No page event closes the dialog while it runs, so the test closes it through its own close method.
    [TestMethod]
    public async Task ASelectWinnerFailingAfterItsDialogClosed_ShowsInThePagesErrorBar()
    {
        var page = Loaded();
        var gate = api.Hold(HttpMethod.Put, nightPath, () => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await RowButton(page, "Game night", "Details").ClickAsync(new());
        var select = Card(page, "Stock").Button("Select Winner").ClickAsync(new());
        page.WaitForAssertion(() => Assert.IsTrue(page.FormDialog().Button("Close").IsBusy()));
        var details = page.FindComponent<EventDetailsDialog>().Instance;
        await page.InvokeAsync(() => EventCallback.Factory.Create(details, details.CloseDetails).InvokeAsync());

        gate.SetResult();
        await select;

        AssertInErrorBar(page, "The winner was not saved. The server had a problem (HTTP 500). Try again.");
        Assert.AreEqual(grandPrix.Id, Assert.ContainsSingle(stored).WinnerActivityId);
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private const string RefreshFailed = "The event was saved, but the list could not be refreshed. Reload the page to see the saved event.";

    private IRenderedComponent<Events> Loaded()
    {
        var page = harness.Render<Events>();
        page.WaitForRows(1);
        return page;
    }

    // The checkbox labels of one filter group in the open dialog, in order.
    private static List<string> FilterOptions(IRenderedComponent<Events> page, string group) =>
        page.FormDialog().QuerySelector($"[role=group][aria-label='{group}']")!
            .QuerySelectorAll("fluent-checkbox").Select(c => c.TextContent.Trim()).ToList();

    // The page's plain messages (its own div), outside any dialog and outside the error bars.
    private static List<string> PlainPageAlerts(IRenderedComponent<Events> page) =>
        page.FindAll("[role=alert]").Where(a => a.Closest("fluent-dialog") is null && a.QuerySelector(".fluent-messagebar-message") is null)
            .Select(a => a.TextContent.Trim()).ToList();

    // The message shows once, in the page's error bar with its Dismiss button, and Dismiss clears it.
    private static void AssertInErrorBar(IRenderedComponent<Events> page, string message)
    {
        page.WaitForAssertion(() => Assert.AreEqual(message, Assert.ContainsSingle(page.PageAlerts())));
        Assert.IsEmpty(PlainPageAlerts(page), "an error-bar message, not the page's plain message");
        var bar = page.FindAll("[role=alert]").Single(a => a.Closest("fluent-dialog") is null);
        bar.Button("Dismiss").Click();
        page.WaitForAssertion(() => Assert.IsEmpty(page.PageAlerts()));
    }

    // Serialized when answered, so a later change to the stored list never alters an answer already sent.
    private static HttpResponseMessage Answer<T>(HttpStatusCode status, T body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body, JsonSerializerOptions.Web), Encoding.UTF8, "application/json") };

    // The last Event the page sent to a route.
    private EventEntity Sent(HttpMethod method, string path) =>
        JsonSerializer.Deserialize<EventEntity>(api.Bodies(method, path).Last()!, JsonSerializerOptions.Web)!;

    private static IElement EventName(IRenderedComponent<Events> page) =>
        page.FormDialog().QuerySelector("fluent-text-field[placeholder='Enter event name...']")!;

    // The Activity on the card that carries the Winner badge in the details dialog.
    private string WinnerInDialog(IRenderedComponent<Events> page)
    {
        var card = page.FormDialog().QuerySelectorAll("fluent-badge").Single(b => b.TextContent.Trim() == "Winner").Closest("fluent-card")!;
        return card.QuerySelectorAll(".fluent-typography").Select(l => l.TextContent.Trim()).Single(t => t == grandPrix.Name || t == stock.Name);
    }
}
