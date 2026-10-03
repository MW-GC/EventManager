using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;

namespace MW_GC.EventManager.Web.Tests;

// The Games page rendered with bUnit against a stub API: failures show a message and never a false success.
[TestClass]
public sealed class GamesErrorStateTests : IDisposable
{
    private const string NameField = "fluent-dialog fluent-text-field[placeholder='Enter game name...']";
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly GameEntity existing = new() { Name = "Existing game" };

    public GamesErrorStateTests()
    {
        api.Json(HttpMethod.Get, "/api/games", new[] { existing })
           .Json(HttpMethod.Get, "/api/activities", Array.Empty<ActivityEntity>())
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>());
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    [TestMethod]
    public async Task CreateThatGets500KeepsTheDialogOpenWithTheTypedNameAndShowsTheError()
    {
        var page = Loaded();
        api.Status(HttpMethod.Post, "/api/games", HttpStatusCode.InternalServerError, "System.Exception: storage down");
        await page.Button("Add Game").ClickAsync(new());
        await page.Find(NameField).ChangeAsync(new ChangeEventArgs { Value = "Typed game" });
        await page.Find("fluent-dialog fluent-text-field[placeholder^='https://']").ChangeAsync(new ChangeEventArgs { Value = "https://example.test/typed" });
        var readsBefore = api.Count(HttpMethod.Get, "/api/games");

        await page.Find("fluent-dialog form").SubmitAsync();

        Assert.AreEqual("Add Game", page.DialogTitle());
        Assert.AreEqual("Typed game", page.Find(NameField).GetAttribute("value"));
        Assert.AreEqual("https://example.test/typed", page.Find("fluent-dialog fluent-text-field[placeholder^='https://']").GetAttribute("value"));
        Assert.AreEqual("The server had a problem (HTTP 500). Try again.", Assert.ContainsSingle(page.Dialog().Alerts()));
        Assert.DoesNotContain("storage down", page.Markup);
        Assert.IsFalse(page.Dialog().Button("Add Game").IsBusy());
        Assert.AreEqual(1, api.Count(HttpMethod.Post, "/api/games"));
        Assert.AreEqual(readsBefore, api.Count(HttpMethod.Get, "/api/games"));
        Assert.Contains("Existing game", Assert.ContainsSingle(page.Rows()));
    }

    [TestMethod]
    public async Task CreateWithTheApiUnreachableKeepsTheDialogOpenAndShowsTheError()
    {
        var page = Loaded();
        api.Offline(HttpMethod.Post, "/api/games");
        await page.Button("Add Game").ClickAsync(new());
        await page.Find(NameField).ChangeAsync(new ChangeEventArgs { Value = "Typed game" });

        await page.Find("fluent-dialog form").SubmitAsync();

        Assert.AreEqual("Add Game", page.DialogTitle());
        Assert.AreEqual("Typed game", page.Find(NameField).GetAttribute("value"));
        Assert.AreEqual("Could not reach the server. Check the connection and try again.", Assert.ContainsSingle(page.Dialog().Alerts()));
        Assert.DoesNotContain("refused", page.Markup);
        Assert.IsFalse(page.Dialog().Button("Add Game").IsBusy());
    }

    [TestMethod]
    public async Task CreateRejectedByTheApiShowsItsOwnText()
    {
        var page = Loaded();
        api.Status(HttpMethod.Post, "/api/games", HttpStatusCode.BadRequest, "Name is required.");
        await page.Button("Add Game").ClickAsync(new());
        await page.Find("fluent-dialog form").SubmitAsync();
        Assert.AreEqual("Name is required.", Assert.ContainsSingle(page.Dialog().Alerts()));
        Assert.AreEqual("Add Game", page.DialogTitle());
    }

    [TestMethod]
    public async Task SuccessfulCreateAfterAFailureClosesTheDialogAndReloads()
    {
        var page = Loaded();
        api.Status(HttpMethod.Post, "/api/games", HttpStatusCode.InternalServerError);
        await page.Button("Add Game").ClickAsync(new());
        await page.Find(NameField).ChangeAsync(new ChangeEventArgs { Value = "Typed game" });
        await page.Find("fluent-dialog form").SubmitAsync();
        Assert.ContainsSingle(page.Dialog().Alerts());

        var created = new GameEntity { Name = "Typed game" };
        api.Json(HttpMethod.Post, "/api/games", created)
           .Json(HttpMethod.Get, "/api/games", new[] { existing, created });
        await page.Find("fluent-dialog form").SubmitAsync();

        Assert.IsFalse(page.HasDialog());
        page.WaitForRows(2);
        Assert.IsTrue(page.Rows().Any(r => r.Contains("Typed game")));
        Assert.IsEmpty(page.PageAlerts());
    }

    [TestMethod]
    public async Task UpdateThatFailsKeepsTheEditDialogAndTheEditedName()
    {
        var page = Loaded();
        api.Status(HttpMethod.Put, $"/api/games/{existing.Id}", HttpStatusCode.ServiceUnavailable);
        await page.Find("fluent-button[title=Edit]").ClickAsync(new());
        await page.Find("fluent-dialog fluent-text-field").ChangeAsync(new ChangeEventArgs { Value = "Renamed" });

        await page.Find("fluent-dialog form").SubmitAsync();

        Assert.AreEqual("Edit Game", page.DialogTitle());
        Assert.AreEqual("Renamed", page.Find("fluent-dialog fluent-text-field").GetAttribute("value"));
        Assert.AreEqual("The server had a problem (HTTP 503). Try again.", Assert.ContainsSingle(page.Dialog().Alerts()));
        Assert.AreEqual(1, api.Count(HttpMethod.Put, $"/api/games/{existing.Id}"));
        var row = Assert.ContainsSingle(page.Rows());
        Assert.Contains("Existing game", row);
        Assert.DoesNotContain("Renamed", row);
    }

    [TestMethod]
    public async Task DeleteThatFailsLeavesTheListAndShowsTheError()
    {
        var page = Loaded();
        api.Status(HttpMethod.Delete, $"/api/games/{existing.Id}", HttpStatusCode.InternalServerError);
        var readsBefore = api.Count(HttpMethod.Get, "/api/games");

        await page.Find("fluent-button[title=Delete]").ClickAsync(new());

        Assert.AreEqual(1, api.Count(HttpMethod.Delete, $"/api/games/{existing.Id}"));
        Assert.Contains("Existing game", Assert.ContainsSingle(page.Rows()));
        Assert.AreEqual(readsBefore, api.Count(HttpMethod.Get, "/api/games"));
        Assert.AreEqual("The game was not deleted. The server had a problem (HTTP 500). Try again.", Assert.ContainsSingle(page.PageAlerts()));
    }

    [TestMethod]
    public async Task FailedLoadShowsTheMessageNotTheSpinnerAndRetryLoadsTheList()
    {
        api.Offline(HttpMethod.Get, "/api/games");

        var page = harness.Render<Games>();
        page.WaitForPageAlert();

        Assert.AreEqual("Games could not be loaded. Could not reach the server. Check the connection and try again.", Assert.ContainsSingle(page.PageAlerts()));
        Assert.DoesNotContain("Loading games...", page.Markup);
        Assert.IsEmpty(page.FindAll("fluent-progress-ring"));
        Assert.IsEmpty(page.Rows());

        api.Json(HttpMethod.Get, "/api/games", new[] { existing });
        await page.Button("Retry").ClickAsync(new());

        page.WaitForRows(1);
        Assert.IsEmpty(page.PageAlerts());
        Assert.Contains("Existing game", Assert.ContainsSingle(page.Rows()));
    }

    [TestMethod]
    public void OneFailedLookupStillShowsTheGamesAndNamesTheList()
    {
        api.Status(HttpMethod.Get, "/api/themes", HttpStatusCode.InternalServerError);

        var page = harness.Render<Games>();
        page.WaitForRows(1);
        page.WaitForPageAlert();

        Assert.Contains("Existing game", Assert.ContainsSingle(page.Rows()));
        Assert.AreEqual("Themes could not be loaded. The server had a problem (HTTP 500). Try again.", Assert.ContainsSingle(page.PageAlerts()));
    }

    private IRenderedComponent<Games> Loaded()
    {
        var page = harness.Render<Games>();
        page.WaitForRows(1);
        return page;
    }
}
