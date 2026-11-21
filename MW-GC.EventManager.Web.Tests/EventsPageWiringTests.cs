using System.Net;
using System.Text;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Pages;

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
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>())
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

    private static Selection Pick(GameEntity game, ActivityEntity act) => new()
    {
        Game = new Game { Id = game.Id, Name = game.Name },
        Activity = new Activity { Id = act.Id, GameId = act.GameId, Name = act.Name }
    };

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

    // ---- Helpers -------------------------------------------------------------------------------

    private IRenderedComponent<Events> Loaded()
    {
        var page = harness.Render<Events>();
        page.WaitForRows(1);
        return page;
    }

    // Serialized when answered, so a later change to the stored list never alters an answer already sent.
    private static HttpResponseMessage Answer<T>(HttpStatusCode status, T body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body, JsonSerializerOptions.Web), Encoding.UTF8, "application/json") };

    // The last Event the page sent to a route.
    private EventEntity Sent(HttpMethod method, string path) =>
        JsonSerializer.Deserialize<EventEntity>(api.Bodies(method, path).Last()!, JsonSerializerOptions.Web)!;

    private static IElement RowButton(IRenderedComponent<Events> page, string item, string title) =>
        page.FindAll("tbody tr.fluent-data-grid-row")
            .Where(r => r.Closest("fluent-dialog") is null)
            .Single(r => r.TextContent.Contains(item))
            .QuerySelector($"fluent-button[title={title}]")!;

    private static IElement EventName(IRenderedComponent<Events> page) =>
        page.FormDialog().QuerySelector("fluent-text-field[placeholder='Enter event name...']")!;

    // The Selection card of one Activity in the details dialog.
    private static IElement Card(IRenderedComponent<Events> page, string activity) =>
        page.FormDialog().QuerySelectorAll("fluent-card")
            .Single(c => c.QuerySelectorAll(".fluent-typography").Any(l => l.TextContent.Trim() == activity));

    // The Activity on the card that carries the Winner badge in the details dialog.
    private string WinnerInDialog(IRenderedComponent<Events> page)
    {
        var card = page.FormDialog().QuerySelectorAll("fluent-badge").Single(b => b.TextContent.Trim() == "Winner").Closest("fluent-card")!;
        return card.QuerySelectorAll(".fluent-typography").Select(l => l.TextContent.Trim()).Single(t => t == grandPrix.Name || t == stock.Name);
    }
}
