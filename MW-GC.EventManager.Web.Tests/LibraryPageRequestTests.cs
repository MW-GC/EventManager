using System.Net;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;

namespace MW_GC.EventManager.Web.Tests;

// Issue #57: the requests the library pages send for a write that succeeds, rendered with bUnit
// against a stub API. Only what no other test pins yet: the first load is in PageSmokeTests, and
// the failure paths (which already pin most paths and several bodies) are in GamesErrorStateTests,
// LibraryPagesErrorStateTests and DialogSafetyTests.
[TestClass]
public sealed class LibraryPageRequestTests : IDisposable
{
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly GameEntity kart = new() { Name = "Kart", Website = "https://example.test/kart" };
    // No activity uses it, so its delete question is the plain one.
    private readonly GameEntity puzzle = new() { Name = "Puzzle" };
    private readonly ActivityEntity race;
    private readonly ThemeEntity spooky = new() { Name = "Spooky" };
    private readonly HolidayEntity halloween = new() { Name = "Halloween" };

    public LibraryPageRequestTests()
    {
        race = new ActivityEntity { Name = "Race", GameId = kart.Id, Rules = "Three laps", ThemeIds = [spooky.Id], Comments = "Old note" };
        api.Json(HttpMethod.Get, "/api/games", new[] { kart, puzzle })
           .Json(HttpMethod.Get, "/api/activities", new[] { race })
           .Json(HttpMethod.Get, "/api/themes", new[] { spooky })
           .Json(HttpMethod.Get, "/api/holidays", new[] { halloween });
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    // The Games failure tests pin the POST path but not what it carries.
    [TestMethod]
    public async Task GamesCreateSendsPostWithTheEnteredValuesThenClosesAndReloads()
    {
        var page = Open("games");
        var created = new GameEntity { Name = "Ghost Race", Website = "https://example.test/ghost" };
        api.Status(HttpMethod.Post, "/api/games", HttpStatusCode.Created)
           .Json(HttpMethod.Get, "/api/games", new[] { kart, puzzle, created });
        var reads = api.Count(HttpMethod.Get, "/api/games");

        await page.Button("Add Game").ClickAsync(new());
        await page.Find("fluent-dialog fluent-text-field[placeholder='Enter game name...']").ChangeAsync(new ChangeEventArgs { Value = "Ghost Race" });
        await page.Find("fluent-dialog fluent-text-field[placeholder^='https://']").ChangeAsync(new ChangeEventArgs { Value = "https://example.test/ghost" });
        await page.Find("fluent-dialog form").SubmitAsync();

        var sent = Json(Assert.ContainsSingle(api.Bodies(HttpMethod.Post, "/api/games")));
        Assert.AreEqual("Ghost Race", sent.GetProperty("name").GetString());
        Assert.AreEqual("https://example.test/ghost", sent.GetProperty("website").GetString());
        Assert.IsFalse(page.HasDialog());
        Assert.AreEqual(reads + 1, api.Count(HttpMethod.Get, "/api/games"));
        page.WaitForRows(3);
        Assert.IsTrue(page.Rows().Any(r => r.Contains("Ghost Race")));
        Assert.IsEmpty(page.PageAlerts());
    }

    // TagPageFailedUpdateKeepsEditDialog pins the PUT path with nothing edited; this pins the edited name.
    [TestMethod]
    [DataRow("themes", "Theme")]
    [DataRow("holidays", "Holiday")]
    public async Task TagPageEditSendsPutToTheItemWithTheEditedNameThenClosesAndReloads(string route, string singular)
    {
        var page = Open(route);
        var id = route == "themes" ? spooky.Id : halloween.Id;
        var path = $"/api/{route}/{id}";
        var reads = api.Count(HttpMethod.Get, $"/api/{route}");
        api.Status(HttpMethod.Put, path, HttpStatusCode.NoContent);
        if (route == "themes") api.Json(HttpMethod.Get, "/api/themes", new[] { new ThemeEntity { Id = id, Name = "Renamed tag" } });
        else api.Json(HttpMethod.Get, "/api/holidays", new[] { new HolidayEntity { Id = id, Name = "Renamed tag" } });

        await page.Find("fluent-button[title=Edit]").ClickAsync(new());
        Assert.AreEqual($"Edit {singular}", page.DialogTitle());
        await page.Find("fluent-dialog fluent-text-field").ChangeAsync(new ChangeEventArgs { Value = "Renamed tag" });
        await page.Find("fluent-dialog form").SubmitAsync();

        var sent = Json(Assert.ContainsSingle(api.Bodies(HttpMethod.Put, path)));
        Assert.AreEqual(id, sent.GetProperty("id").GetGuid());
        Assert.AreEqual("Renamed tag", sent.GetProperty("name").GetString());
        Assert.IsFalse(page.HasDialog());
        Assert.AreEqual(reads + 1, api.Count(HttpMethod.Get, $"/api/{route}"));
        page.WaitForAssertion(() => Assert.Contains("Renamed tag", Assert.ContainsSingle(page.Rows())));
        Assert.IsEmpty(page.PageAlerts());
    }

    // No other test edits an activity. This one also ticks a holiday, so a checkbox value is pinned too.
    [TestMethod]
    public async Task ActivitiesEditSendsPutToTheActivityWithTheEditedValuesThenClosesAndReloads()
    {
        var page = Open("activities");
        var path = $"/api/activities/{race.Id}";
        var renamed = new ActivityEntity { Id = race.Id, Name = "Grand race", GameId = kart.Id, Rules = "Five laps", ThemeIds = [spooky.Id], HolidayIds = [halloween.Id], Comments = race.Comments };
        api.Status(HttpMethod.Put, path, HttpStatusCode.NoContent)
           .Json(HttpMethod.Get, "/api/activities", new[] { renamed });
        var reads = api.Count(HttpMethod.Get, "/api/activities");

        await RowButton(page, "Race", "Edit").ClickAsync(new());
        Assert.AreEqual("Edit Activity", page.DialogTitle());
        await page.Find("fluent-dialog fluent-text-field").ChangeAsync(new ChangeEventArgs { Value = "Grand race" });
        await page.Find("fluent-dialog fluent-text-area[placeholder='Rules for this activity...']").ChangeAsync(new ChangeEventArgs { Value = "Five laps" });
        await page.FindAll("fluent-dialog fluent-checkbox").Single(c => c.TextContent.Contains("Halloween"))
            .TriggerEventAsync("oncheckedchange", new CheckboxChangeEventArgs { Checked = true });
        await page.Find("fluent-dialog form").SubmitAsync();

        var sent = Json(Assert.ContainsSingle(api.Bodies(HttpMethod.Put, path)));
        Assert.AreEqual(race.Id, sent.GetProperty("id").GetGuid());
        Assert.AreEqual("Grand race", sent.GetProperty("name").GetString());
        Assert.AreEqual("Five laps", sent.GetProperty("rules").GetString());
        Assert.AreEqual(halloween.Id, Assert.ContainsSingle(Guids(sent, "holidayIds")));
        // The fields the user did not touch go back unchanged, the comments included.
        Assert.AreEqual(kart.Id, sent.GetProperty("gameId").GetGuid());
        Assert.AreEqual(spooky.Id, Assert.ContainsSingle(Guids(sent, "themeIds")));
        Assert.AreEqual("Old note", sent.GetProperty("comments").GetString());
        Assert.IsFalse(page.HasDialog());
        Assert.AreEqual(reads + 1, api.Count(HttpMethod.Get, "/api/activities"));
        page.WaitForAssertion(() => Assert.Contains("Grand race", Assert.ContainsSingle(page.Rows())));
        Assert.IsEmpty(page.PageAlerts());
    }

    // ActivitiesFailedCommentSaveKeepsTextAndButton pins the PATCH path and that the text is sent;
    // this pins the body shape the API reads, {"comments": ...}, and nothing else.
    [TestMethod]
    public async Task ActivitiesSaveCommentsSendsPatchWithOnlyTheCommentsProperty()
    {
        var page = Open("activities");
        var path = $"/api/activities/{race.Id}/comments";
        api.Status(HttpMethod.Patch, path, HttpStatusCode.NoContent);

        await RowButton(page, "Race", "Details").ClickAsync(new());
        await page.Find("fluent-dialog fluent-text-area").ChangeAsync(new ChangeEventArgs { Value = "Ran long" });
        await page.Dialog().Button("Save Comments").ClickAsync(new());

        var sent = Json(Assert.ContainsSingle(api.Bodies(HttpMethod.Patch, path)));
        var property = Assert.ContainsSingle(sent.EnumerateObject().ToList());
        Assert.AreEqual("comments", property.Name);
        Assert.AreEqual("Ran long", property.Value.GetString());
        Assert.AreEqual(0, api.Count(HttpMethod.Put, $"/api/activities/{race.Id}"));
        Assert.IsEmpty(page.Dialog().Alerts());
    }

    // The failure tests pin each DELETE path; a confirmed delete that succeeds and reloads is pinned
    // only for Themes (DialogSafetyTests.Delete_ConfirmRunsDelete_AndReloads). These are the other three.
    [TestMethod]
    [DataRow("games", "Puzzle")]
    [DataRow("activities", "Race")]
    [DataRow("holidays", "Halloween")]
    public async Task ConfirmedDeleteSendsDeleteToTheItemThenReloads(string route, string item)
    {
        var page = Open(route);
        Guid id;
        switch (route)
        {
            case "games":
                id = puzzle.Id;
                api.Json(HttpMethod.Get, "/api/games", new[] { kart });
                break;
            case "activities":
                id = race.Id;
                api.Json(HttpMethod.Get, "/api/activities", Array.Empty<ActivityEntity>());
                break;
            default:
                id = halloween.Id;
                api.Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>());
                break;
        }
        var path = $"/api/{route}/{id}";
        api.Status(HttpMethod.Delete, path, HttpStatusCode.NoContent);
        var reads = api.Count(HttpMethod.Get, $"/api/{route}");

        await RowButton(page, item, "Delete").ClickAsync(new());
        Assert.AreEqual(0, api.Count(HttpMethod.Delete, path));
        await page.ConfirmAsync();

        Assert.IsNull(Assert.ContainsSingle(api.Bodies(HttpMethod.Delete, path)));
        Assert.HasCount(1, api.Requests.Where(r => r.Method == HttpMethod.Delete).ToList());
        Assert.AreEqual(reads + 1, api.Count(HttpMethod.Get, $"/api/{route}"));
        // An empty grid still renders its empty-content row, so look for the deleted name instead.
        page.WaitForAssertion(() => Assert.IsFalse(page.Rows().Any(r => r.Contains(item))));
        Assert.IsFalse(page.HasConfirmation());
        Assert.IsEmpty(page.PageAlerts());
    }

    private IRenderedComponent<IComponent> Open(string route)
    {
        IRenderedComponent<IComponent> page = route switch
        {
            "games" => harness.Render<Games>(),
            "activities" => harness.Render<Activities>(),
            "themes" => harness.Render<Themes>(),
            _ => harness.Render<Holidays>()
        };
        page.WaitForRows(route == "games" ? 2 : 1);
        return page;
    }

    private static IElement RowButton(IRenderedComponent<IComponent> page, string item, string title) =>
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

    private static List<Guid> Guids(JsonElement body, string property) =>
        body.GetProperty(property).EnumerateArray().Select(e => e.GetGuid()).ToList();
}
