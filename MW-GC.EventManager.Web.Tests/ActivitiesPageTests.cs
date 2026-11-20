using System.Net;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using MW_GC.EventManager.Web.Shared.Library;

namespace MW_GC.EventManager.Web.Tests;

// Issue #126: the Activities page on the shared Library page. A new Activity needs a Game: the form says so
// under the Game field and sends nothing until one is chosen. The rest is the page as it was: search with
// the Game filter, and no card view.
[TestClass]
public sealed class ActivitiesPageTests : IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);
    private const string GameRequired = "Game is required.";
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly GameEntity kart = new() { Name = "Kart" };
    private readonly GameEntity brawl = new() { Name = "Brawl" };
    private readonly ThemeEntity spooky = new() { Name = "Spooky" };
    private readonly ActivityEntity race;
    private readonly ActivityEntity sprint;
    private readonly ActivityEntity stock;

    public ActivitiesPageTests()
    {
        race = new ActivityEntity { Name = "Race", GameId = kart.Id, Description = "Three laps", ThemeIds = [spooky.Id], Comments = "Old note" };
        sprint = new ActivityEntity { Name = "Sprint", GameId = kart.Id };
        stock = new ActivityEntity { Name = "Stock", GameId = brawl.Id };
        api.Json(HttpMethod.Get, "/api/games", new[] { kart, brawl })
           .Json(HttpMethod.Get, "/api/activities", new[] { race, sprint, stock })
           .Json(HttpMethod.Get, "/api/themes", new[] { spooky })
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>());
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    // ---- Game required -------------------------------------------------------------------------

    [TestMethod]
    public async Task AddWithNoGame_ShowsGameIsRequiredUnderTheGameField_AndSendsNothing()
    {
        var page = Open();
        await page.Button("Add Activity").ClickAsync(new());
        // Nothing is checked when the editor opens.
        Assert.IsEmpty(page.FormDialog().Alerts());
        await NameField(page).ChangeAsync(new ChangeEventArgs { Value = "Ghost lap" });

        await Submit(page);

        Assert.AreEqual("Add Activity", page.DialogTitle());
        Assert.AreEqual(GameRequired, Assert.ContainsSingle(page.FormDialog().Alerts()));
        Assert.AreEqual(GameRequired, Assert.ContainsSingle(GameSelect(page).ParentElement!.Alerts()));
        AssertNoWrites();
        Assert.IsFalse(page.FormDialog().Button("Add Activity").IsBusy());
    }

    [TestMethod]
    public async Task AddWithNoGame_ChoosingAGame_ClearsTheMessage_AndTheSaveSendsOneRequestWithThatGame()
    {
        var page = Open();
        api.Status(HttpMethod.Post, "/api/activities", HttpStatusCode.Created);
        await page.Button("Add Activity").ClickAsync(new());
        await NameField(page).ChangeAsync(new ChangeEventArgs { Value = "Ghost lap" });
        await Submit(page);
        Assert.AreEqual(GameRequired, Assert.ContainsSingle(page.FormDialog().Alerts()));

        await GameOption(page, brawl).ClickAsync(new());
        Assert.IsEmpty(page.FormDialog().Alerts());
        AssertNoWrites();

        await Submit(page);
        var sent = Json(Assert.ContainsSingle(api.Bodies(HttpMethod.Post, "/api/activities")));
        Assert.AreEqual(brawl.Id, sent.GetProperty("gameId").GetGuid());
        Assert.AreEqual("Ghost lap", sent.GetProperty("name").GetString());
        Assert.IsFalse(page.HasFormDialog());
    }

    [TestMethod]
    public async Task AddWithAGame_SavesWithoutAMessage_ClosesAndReloads()
    {
        var page = Open();
        var created = new ActivityEntity { Name = "Ghost lap", GameId = kart.Id };
        api.Status(HttpMethod.Post, "/api/activities", HttpStatusCode.Created)
           .Json(HttpMethod.Get, "/api/activities", new[] { race, sprint, stock, created });
        var reads = api.Count(HttpMethod.Get, "/api/activities");

        await page.Button("Add Activity").ClickAsync(new());
        await GameOption(page, kart).ClickAsync(new());
        await NameField(page).ChangeAsync(new ChangeEventArgs { Value = "Ghost lap" });
        Assert.IsEmpty(page.FormDialog().Alerts());
        await Submit(page);

        Assert.AreEqual(kart.Id, Json(Assert.ContainsSingle(api.Bodies(HttpMethod.Post, "/api/activities"))).GetProperty("gameId").GetGuid());
        Assert.IsFalse(page.HasFormDialog());
        Assert.AreEqual(reads + 1, api.Count(HttpMethod.Get, "/api/activities"));
        page.WaitForRows(4);
        Assert.IsEmpty(page.PageAlerts());
    }

    [TestMethod]
    public async Task Edit_ShowsTheGameItCannotChange_AndNoGameMessage()
    {
        var page = Open();
        await RowButton(page, "Race", "Edit").ClickAsync(new());

        Assert.AreEqual("Edit Activity", page.DialogTitle());
        Assert.AreEqual(kart.Id.ToString(), GameSelect(page).GetAttribute("current-value"));
        Assert.IsTrue(GameSelect(page).HasAttribute("disabled"));
        Assert.IsEmpty(page.FormDialog().Alerts());
    }

    // The message has one home in the form (LibraryRules.GameRequired). The API refuses the same case with its
    // own words, which this page leaves as they are.
    [TestMethod]
    public void GameRule_MessageLivesInOnePlace_AndTheApiRefusesTheSameCase()
    {
        Assert.AreEqual(GameRequired, LibraryRules.Game(Guid.Empty));
        Assert.AreEqual(LibraryRules.GameRequired, LibraryRules.Game(Guid.Empty));
        Assert.IsNull(LibraryRules.Game(Guid.NewGuid()));

        Assert.AreEqual("GameId is required.", ActivityValidator.Validate(new ActivityEntity { Name = "Ghost lap" }));
        Assert.IsNull(ActivityValidator.Validate(new ActivityEntity { Name = "Ghost lap", GameId = Guid.NewGuid() }));
    }

    // The details dialog and its Comments panel are covered in ActivityCommentsPanelTests (#131).

    // ---- Search, the Game filter and the view --------------------------------------------------

    [TestMethod]
    public async Task SearchAndGameFilter_NarrowTheListTogether()
    {
        var page = Open();

        await GameFilter(page).QuerySelectorAll("fluent-option")
            .Single(o => o.GetAttribute("value") == kart.Id.ToString()).ClickAsync(new());
        page.WaitForAssertion(() => Assert.HasCount(2, page.Rows()), Settle);
        Assert.IsFalse(page.Rows().Any(r => r.Contains("Stock")));

        await Search(page).InputAsync(new ChangeEventArgs { Value = "spr" });
        page.WaitForAssertion(() => Assert.Contains("Sprint", Assert.ContainsSingle(page.Rows())), Settle);
        AssertNoWrites();
    }

    // The old page had a table only, so the shared page offers it no card view.
    [TestMethod]
    public void TheListIsATable_WithNoCardView_AsBefore()
    {
        var page = Open();

        Assert.IsEmpty(page.FindAll("fluent-button[title='Card view']"));
        Assert.IsEmpty(page.FindAll("fluent-button[title='Table view']"));
        Assert.HasCount(3, page.Rows());
    }

    // ---- Helpers -------------------------------------------------------------------------------

    // Renders the page and waits for the Activities and all three lists their rows and editor read.
    private IRenderedComponent<Activities> Open()
    {
        var page = harness.Render<Activities>();
        page.WaitForAssertion(() => Assert.AreEqual(4, api.Requests.Count(r => r.Method == HttpMethod.Get)));
        page.WaitForRows(3);
        page.WaitForAssertion(() => Assert.IsFalse(page.Rows().Any(r => r.Contains('—'))));
        return page;
    }

    private void AssertNoWrites() => Assert.IsEmpty(api.Requests.Where(r => r.Method != HttpMethod.Get).ToList());

    private static Task Submit(IRenderedComponent<Activities> page) => page.FormDialog().QuerySelector("form")!.SubmitAsync();

    private static IElement NameField(IRenderedComponent<Activities> page) =>
        page.Find("fluent-dialog fluent-text-field[placeholder='Enter activity name...']");

    private static IElement GameSelect(IRenderedComponent<Activities> page) => page.FormDialog().QuerySelector("fluent-select")!;

    private static IElement GameOption(IRenderedComponent<Activities> page, GameEntity game) =>
        GameSelect(page).QuerySelectorAll("fluent-option").Single(o => o.GetAttribute("value") == game.Id.ToString());

    private static IElement GameFilter(IRenderedComponent<Activities> page) =>
        page.FindAll("fluent-select").Single(s => s.Closest("fluent-dialog") is null);

    private static IElement Search(IRenderedComponent<Activities> page) =>
        page.FindAll("fluent-search").Single(s => s.Closest("fluent-dialog") is null);

    private static IElement RowButton(IRenderedComponent<Activities> page, string item, string title) =>
        page.FindAll("tbody tr.fluent-data-grid-row")
            .Where(r => r.Closest("fluent-dialog") is null)
            .Single(r => r.TextContent.Contains(item))
            .QuerySelector($"fluent-button[title={title}]")!;

    private static JsonElement Json(string? body)
    {
        Assert.IsNotNull(body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
