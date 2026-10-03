using System.Net;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Pages;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Tests;

// The Events page rendered with bUnit against a stub API: lookups fail independently, and Select
// Winner, Delete and Save Comments never report a success the API did not confirm.
[TestClass]
public sealed class EventsErrorStateTests : IDisposable
{
    private const string ServerError = "The server had a problem (HTTP 500). Try again.";
    private const string Unreachable = "Could not reach the server. Check the connection and try again.";
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly Guid gameA = Guid.NewGuid();
    private readonly Guid gameB = Guid.NewGuid();
    private readonly ActivityEntity first;
    private readonly ActivityEntity second;
    private readonly EventEntity ev;

    public EventsErrorStateTests()
    {
        first = new ActivityEntity { Name = "First", GameId = gameA, Comments = "Old note" };
        second = new ActivityEntity { Name = "Second", GameId = gameB };
        ev = new EventEntity
        {
            Name = "Game night",
            Selections =
            [
                new() { Game = new Game { Id = gameA, Name = "A" }, Activity = new Activity { Id = first.Id, GameId = gameA, Name = first.Name } },
                new() { Game = new Game { Id = gameB, Name = "B" }, Activity = new Activity { Id = second.Id, GameId = gameB, Name = second.Name } }
            ],
            WinnerActivityId = first.Id
        };
        api.Json(HttpMethod.Get, "/api/events", new[] { ev })
           .Json(HttpMethod.Get, "/api/games", new[] { new GameEntity { Id = gameA, Name = "A" }, new GameEntity { Id = gameB, Name = "B" } })
           .Json(HttpMethod.Get, "/api/activities", new[] { first, second })
           .Json(HttpMethod.Get, "/api/themes", new[] { new ThemeEntity { Name = "Spooky" } })
           .Json(HttpMethod.Get, "/api/holidays", new[] { new HolidayEntity { Name = "Halloween" } });
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    [TestMethod]
    [DataRow("themes", "Themes")]
    [DataRow("holidays", "Holidays")]
    public async Task FailedLookupStillShowsTheEventsAndNamesTheList(string route, string name)
    {
        api.Status(HttpMethod.Get, $"/api/{route}", HttpStatusCode.InternalServerError);

        var page = Loaded();
        page.WaitForPageAlert();

        Assert.Contains("Game night", Assert.ContainsSingle(page.Rows()));
        Assert.AreEqual($"{name} could not be loaded. {ServerError}", Assert.ContainsSingle(page.PageAlerts()));
        // Games and activities still loaded: the customize dialog lists both games as filters.
        await page.Button("Create Event").ClickAsync(new());
        var gameFilters = page.Dialog().QuerySelectorAll("fluent-checkbox").Select(c => c.TextContent.Trim()).ToList();
        CollectionAssert.IsSubsetOf(new[] { "A", "B" }, gameFilters);
    }

    [TestMethod]
    public void BothLookupsFailingAreBothNamed()
    {
        api.Offline(HttpMethod.Get, "/api/themes").Offline(HttpMethod.Get, "/api/holidays");
        var page = Loaded();
        page.WaitForPageAlert();
        Assert.ContainsSingle(page.Rows());
        Assert.AreEqual($"Themes and Holidays could not be loaded. {Unreachable}", Assert.ContainsSingle(page.PageAlerts()));
    }

    [TestMethod]
    public async Task FailedEventLoadShowsTheMessageAndRetryLoadsTheList()
    {
        api.Offline(HttpMethod.Get, "/api/events");

        var page = harness.Render<Events>();
        page.WaitForPageAlert();

        Assert.IsEmpty(page.Rows());
        Assert.IsEmpty(page.FindAll("fluent-progress-ring"));
        Assert.AreEqual($"Events could not be loaded. {Unreachable}", Assert.ContainsSingle(page.PageAlerts()));

        api.Json(HttpMethod.Get, "/api/events", new[] { ev });
        await page.Button("Retry").ClickAsync(new());

        page.WaitForRows(1);
        Assert.IsEmpty(page.PageAlerts());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedSelectWinnerKeepsThePreviousWinnerAndShowsTheError(bool offline)
    {
        var page = Loaded();
        await page.Find("fluent-button[title=Details]").ClickAsync(new());
        var path = $"/api/events/{ev.Id}";
        if (offline) api.Offline(HttpMethod.Put, path);
        else api.Status(HttpMethod.Put, path, HttpStatusCode.InternalServerError);
        var reads = api.Count(HttpMethod.Get, "/api/events");

        await page.Dialog().Button("Select Winner").ClickAsync(new());

        Assert.AreEqual("Game night", page.DialogTitle());
        Assert.AreEqual("First", WinnerInDialog(page));
        Assert.Contains("A — First", Assert.ContainsSingle(page.Rows()));
        Assert.AreEqual(reads, api.Count(HttpMethod.Get, "/api/events"));
        Assert.AreEqual($"The winner was not saved. {(offline ? Unreachable : ServerError)}", Assert.ContainsSingle(page.Dialog().Alerts()));
        Assert.AreEqual(1, api.Count(HttpMethod.Put, path));
        Assert.Contains(second.Id.ToString(), Assert.ContainsSingle(api.Bodies(HttpMethod.Put, path))!);
    }

    [TestMethod]
    public async Task SelectWinnerSucceedsAfterAFailureAndClearsTheError()
    {
        var page = Loaded();
        await page.Find("fluent-button[title=Details]").ClickAsync(new());
        api.Status(HttpMethod.Put, $"/api/events/{ev.Id}", HttpStatusCode.InternalServerError);
        await page.Dialog().Button("Select Winner").ClickAsync(new());
        Assert.ContainsSingle(page.Dialog().Alerts());

        var saved = new EventEntity { Id = ev.Id, Name = ev.Name, Selections = ev.Selections, WinnerActivityId = second.Id };
        api.Json(HttpMethod.Put, $"/api/events/{ev.Id}", saved)
           .Json(HttpMethod.Get, "/api/events", new[] { saved });
        await page.Dialog().Button("Select Winner").ClickAsync(new());

        Assert.IsEmpty(page.Dialog().Alerts());
        Assert.AreEqual("Second", WinnerInDialog(page));
        Assert.Contains("B — Second", Assert.ContainsSingle(page.Rows()));
    }

    [TestMethod]
    public async Task FailedDeleteLeavesTheListAndShowsTheError()
    {
        var page = Loaded();
        api.Status(HttpMethod.Delete, $"/api/events/{ev.Id}", HttpStatusCode.InternalServerError);
        var reads = api.Count(HttpMethod.Get, "/api/events");

        await page.Find("fluent-button[title=Delete]").ClickAsync(new());
        await page.ConfirmAsync();

        Assert.AreEqual(1, api.Count(HttpMethod.Delete, $"/api/events/{ev.Id}"));
        Assert.Contains("Game night", Assert.ContainsSingle(page.Rows()));
        Assert.AreEqual(reads, api.Count(HttpMethod.Get, "/api/events"));
        Assert.AreEqual($"The event was not deleted. {ServerError}", Assert.ContainsSingle(page.PageAlerts()));
    }

    [TestMethod]
    public async Task FailedCommentSaveUsesTheSharedMessageAndKeepsTheText()
    {
        var page = Loaded();
        await page.Find("fluent-button[title=Details]").ClickAsync(new());
        var path = $"/api/activities/{first.Id}/comments";
        api.Status(HttpMethod.Patch, path, HttpStatusCode.InternalServerError);
        await page.Find("fluent-dialog fluent-text-area").ChangeAsync(new ChangeEventArgs { Value = "Typed but not saved" });

        await page.Dialog().Button("Save Comments").ClickAsync(new());

        Assert.Contains("Typed but not saved", Assert.ContainsSingle(api.Bodies(HttpMethod.Patch, path))!);
        Assert.AreEqual(ApiResult.CommentsNotSaved(ServerError), Assert.ContainsSingle(page.Dialog().Alerts()));
        Assert.AreEqual("Typed but not saved", page.Find("fluent-dialog fluent-text-area").GetAttribute("value"));
        // Still dirty: Save Comments stays enabled for a retry.
        Assert.IsFalse(page.Dialog().Button("Save Comments").IsBusy());
    }

    [TestMethod]
    public async Task RejectedEventSaveShowsTheApisTextInTheDialog()
    {
        var page = Loaded();
        await page.Find("fluent-button[title=Edit]").ClickAsync(new());
        api.Status(HttpMethod.Put, $"/api/events/{ev.Id}", HttpStatusCode.BadRequest, "An activity may only be selected once.");

        await page.Dialog().Button("Update Event").ClickAsync(new());

        Assert.AreEqual(1, api.Count(HttpMethod.Put, $"/api/events/{ev.Id}"));
        Assert.AreEqual("Edit Event", page.DialogTitle());
        Assert.AreEqual("The event was not saved. An activity may only be selected once.", Assert.ContainsSingle(page.Dialog().Alerts()));
        Assert.AreEqual("Game night", page.Find("fluent-dialog fluent-text-field[placeholder='Enter event name...']").GetAttribute("value"));
    }

    private IRenderedComponent<Events> Loaded()
    {
        var page = harness.Render<Events>();
        page.WaitForRows(1);
        return page;
    }

    // The activity on the card that carries the Winner badge in the details dialog.
    private string WinnerInDialog(IRenderedComponent<Events> page)
    {
        IElement card = page.Dialog().QuerySelectorAll("fluent-badge").Single(b => b.TextContent.Trim() == "Winner").Closest("fluent-card")!;
        return card.QuerySelectorAll(".fluent-typography").Select(l => l.TextContent.Trim()).Single(t => t == first.Name || t == second.Name);
    }
}
