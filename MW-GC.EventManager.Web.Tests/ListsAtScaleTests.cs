using System.Net;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Pages;

namespace MW_GC.EventManager.Web.Tests;

// Issue #55, rendered with bUnit against a stub API: Events open newest first and the library grids by
// name; every search filters as you type (debounced, no Enter); the Theme and Holiday checkbox lists sit
// in the same scroll box as the Games list; the Games page shows the same activity counts as before.
[TestClass]
public sealed class ListsAtScaleTests : IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    // The API returns every list in no particular order; these are deliberately not sorted.
    private readonly GameEntity kart = new() { Name = "Kart" };
    private readonly GameEntity alpha = new() { Name = "Alpha" };
    private readonly GameEntity puzzle = new() { Name = "Puzzle" };
    private readonly ActivityEntity kartSprint;
    private readonly ActivityEntity alphaZone;
    private readonly ActivityEntity kartBattle;
    private readonly ActivityEntity alphaBrawl;
    private readonly ThemeEntity[] themes = [new() { Name = "Spooky" }, new() { Name = "Cozy" }, new() { Name = "Retro" }];
    private readonly HolidayEntity[] holidays = [new() { Name = "Yule" }, new() { Name = "Easter" }, new() { Name = "Halloween" }];
    private readonly EventEntity[] events;

    public ListsAtScaleTests()
    {
        kartSprint = new ActivityEntity { Name = "Sprint", GameId = kart.Id };
        alphaZone = new ActivityEntity { Name = "Zone", GameId = alpha.Id };
        kartBattle = new ActivityEntity { Name = "Battle", GameId = kart.Id };
        alphaBrawl = new ActivityEntity { Name = "Brawl", GameId = alpha.Id };
        events =
        [
            Event("March night", new DateTimeOffset(2026, 3, 1, 19, 0, 0, TimeSpan.Zero)),
            Event("October night", new DateTimeOffset(2026, 10, 1, 19, 0, 0, TimeSpan.Zero)),
            Event("January night", new DateTimeOffset(2026, 1, 1, 19, 0, 0, TimeSpan.Zero)),
            Event("December night", new DateTimeOffset(2025, 12, 1, 19, 0, 0, TimeSpan.Zero)),
            Event("July night", new DateTimeOffset(2026, 7, 1, 19, 0, 0, TimeSpan.Zero)),
        ];
        api.Json(HttpMethod.Get, "/api/games", new[] { kart, puzzle, alpha })
           .Json(HttpMethod.Get, "/api/activities", new[] { kartSprint, alphaZone, kartBattle, alphaBrawl })
           .Json(HttpMethod.Get, "/api/themes", themes)
           .Json(HttpMethod.Get, "/api/holidays", holidays)
           .Json(HttpMethod.Get, "/api/events", events);
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    private EventEntity Event(string name, DateTimeOffset date) => new()
    {
        Name = name,
        Date = date,
        Selections = [new() { Game = new Game { Id = kart.Id, Name = kart.Name }, Activity = new Activity { Id = kartSprint.Id, GameId = kart.Id, Name = kartSprint.Name } }],
        WinnerActivityId = kartSprint.Id
    };

    // ---- Default order -------------------------------------------------------------------------

    [TestMethod]
    public void Events_OpenNewestDateFirst()
    {
        var page = Open("events");
        Assert.AreEqual("October night, July night, March night, January night, December night",
            Order(page, events.Select(e => e.Name)));
    }

    [TestMethod]
    public async Task Events_DateHeaderClick_StillReSorts()
    {
        var page = Open("events");
        await SortHeader(page, "Date").ClickAsync(new MouseEventArgs());
        page.WaitForAssertion(() => Assert.AreEqual("December night, January night, March night, July night, October night",
            Order(page, events.Select(e => e.Name))));
    }

    [TestMethod]
    public async Task Events_NewEventAppearsAtTheTop()
    {
        var page = Open("events");
        var created = Event("Tonight", new DateTimeOffset(2026, 10, 20, 19, 0, 0, TimeSpan.Zero));
        api.Status(HttpMethod.Post, "/api/events", HttpStatusCode.Created)
           .Json(HttpMethod.Get, "/api/events", events.Append(created).ToArray());

        await page.Button("Create Event").ClickAsync(new());
        await page.Find("fluent-dialog fluent-text-field[placeholder='Enter event name...']").InputAsync(new ChangeEventArgs { Value = "Tonight" });
        await page.FormDialog().Button("Create Event").ClickAsync(new());

        page.WaitForAssertion(() => Assert.HasCount(6, page.Rows()));
        Assert.StartsWith("Tonight", page.Rows()[0]);
    }

    [TestMethod]
    [DataRow("games", "Alpha,Kart,Puzzle")]
    [DataRow("themes", "Cozy,Retro,Spooky")]
    [DataRow("holidays", "Easter,Halloween,Yule")]
    public void LibraryGrids_OpenSortedByName(string route, string expected)
    {
        var page = Open(route);
        Assert.AreEqual(expected.Replace(",", ", "), Order(page, expected.Split(',')));
    }

    // Activities keep their Game-then-Name order, now as the order the grid opens in.
    [TestMethod]
    public void Activities_OpenSortedByGameThenName()
    {
        var page = Open("activities");
        Assert.AreEqual("Brawl, Zone, Battle, Sprint", Order(page, ["Brawl", "Zone", "Battle", "Sprint"]));
    }

    [TestMethod]
    public async Task Themes_NameHeaderClick_StillReSorts()
    {
        var page = Open("themes");
        await SortHeader(page, "Name").ClickAsync(new MouseEventArgs());
        page.WaitForAssertion(() => Assert.AreEqual("Spooky, Retro, Cozy", Order(page, ["Spooky", "Retro", "Cozy"])));
    }

    // ---- Search as you type ----------------------------------------------------------------------

    [TestMethod]
    [DataRow("events", "oct", "October night")]
    [DataRow("games", "puz", "Puzzle")]
    [DataRow("activities", "spr", "Sprint")]
    [DataRow("themes", "ret", "Retro")]
    [DataRow("holidays", "eas", "Easter")]
    public async Task Search_FiltersWhileTyping_WithoutEnter(string route, string typed, string match)
    {
        var page = Open(route);
        var all = page.Rows().Count;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Search(page).InputAsync(new ChangeEventArgs { Value = typed });
        // Debounced: one keystroke does not refilter at once (only checked while well inside the delay).
        if (clock.ElapsedMilliseconds < 200) Assert.HasCount(all, page.Rows());

        page.WaitForAssertion(() => Assert.Contains(match, Assert.ContainsSingle(page.Rows())), Settle);
        Assert.IsTrue(clock.ElapsedMilliseconds >= 250, $"the search refiltered {clock.ElapsedMilliseconds} ms after the input, before its debounce delay");
    }

    [TestMethod]
    [DataRow("events")]
    [DataRow("games")]
    [DataRow("activities")]
    [DataRow("themes")]
    [DataRow("holidays")]
    public async Task Search_ClearedBox_RestoresTheFullList(string route)
    {
        var page = Open(route);
        var all = page.Rows();
        await Search(page).InputAsync(new ChangeEventArgs { Value = "zzz-no-match" });
        // An empty grid shows one placeholder row (its empty-content cell), no item rows.
        page.WaitForAssertion(() => Assert.IsEmpty(page.FindAll("tbody tr.fluent-data-grid-row")
            .Where(r => r.Closest("fluent-dialog") is null && r.QuerySelector(".empty-content-cell") is null)), Settle);

        await Search(page).InputAsync(new ChangeEventArgs { Value = "" });

        page.WaitForAssertion(() => CollectionAssert.AreEqual(all, page.Rows()), Settle);
    }

    // ---- Checkbox lists ----------------------------------------------------------------------------

    [TestMethod]
    [DataRow("events", "Filter by games")]
    [DataRow("events", "Filter by themes")]
    [DataRow("events", "Filter by holidays")]
    [DataRow("activities", "Themes")]
    [DataRow("activities", "Holidays")]
    public async Task CheckboxLists_SitInAScrollBox(string route, string group)
    {
        var page = Open(route);
        await page.Button(route == "events" ? "Create Event" : "Add Activity").ClickAsync(new());

        var box = page.FormDialog().QuerySelectorAll("[role=group]").Single(g => g.GetAttribute("aria-label") == group);
        var style = box.GetAttribute("style") ?? string.Empty;
        StringAssert.Contains(style, "max-height: 160px");
        StringAssert.Contains(style, "overflow-y: auto");
        Assert.IsNotEmpty(box.QuerySelectorAll("fluent-checkbox"));
    }

    [TestMethod]
    public async Task ActivityEditForm_CheckboxLists_SitInAScrollBox()
    {
        var page = Open("activities");
        await Row(page, "Sprint").QuerySelector("fluent-button[title=Edit]")!.ClickAsync(new());

        foreach (var group in new[] { "Themes", "Holidays" })
        {
            var box = page.FormDialog().QuerySelectorAll("[role=group]").Single(g => g.GetAttribute("aria-label") == group);
            StringAssert.Contains(box.GetAttribute("style") ?? string.Empty, "max-height: 160px");
        }
    }

    // ---- Games activity counts ---------------------------------------------------------------------

    [TestMethod]
    public void GamesCounts_MatchTheActivitiesOfEachGame_IncludingNone()
    {
        var page = Open("games");
        Assert.AreEqual("2", Count(page, "Kart"));
        Assert.AreEqual("2", Count(page, "Alpha"));
        Assert.AreEqual("0", Count(page, "Puzzle"));
    }

    [TestMethod]
    public void GamesCounts_WithNoActivitiesAtAll_AreZero()
    {
        api.Json(HttpMethod.Get, "/api/activities", Array.Empty<ActivityEntity>());
        var page = Open("games");
        Assert.AreEqual("0", Count(page, "Kart"));
        Assert.AreEqual("0", Count(page, "Alpha"));
        Assert.AreEqual("0", Count(page, "Puzzle"));
    }

    [TestMethod]
    public async Task GamesCounts_InCardViewAndTheDeleteQuestion()
    {
        var page = Open("games");
        await page.Find("fluent-button[title='Card view']").ClickAsync(new());
        var kartCard = page.FindAll("fluent-card").Last(c => c.QuerySelector("fluent-button") is not null && c.TextContent.Contains("Kart"));
        StringAssert.Contains(kartCard.QuerySelector("fluent-badge")!.TextContent, "2 activities");
        await page.Find("fluent-button[title='Table view']").ClickAsync(new());

        await Row(page, "Kart").QuerySelector("fluent-button[title=Delete]")!.ClickAsync(new());
        StringAssert.Contains(page.Confirmation().TextContent, "Kart has 2 activities.");
    }

    // A delete reloads both lists; the counts follow what the API now says.
    [TestMethod]
    public async Task GamesCounts_AfterADelete_FollowTheReloadedActivities()
    {
        var page = Open("games");
        api.Status(HttpMethod.Delete, $"/api/games/{puzzle.Id}", HttpStatusCode.NoContent)
           .Json(HttpMethod.Get, "/api/games", new[] { kart, alpha })
           .Json(HttpMethod.Get, "/api/activities", new[] { kartSprint, alphaZone, alphaBrawl });

        await Row(page, "Puzzle").QuerySelector("fluent-button[title=Delete]")!.ClickAsync(new());
        await page.ConfirmAsync();

        page.WaitForAssertion(() => Assert.HasCount(2, page.Rows()));
        Assert.AreEqual("1", Count(page, "Kart"));
        Assert.AreEqual("2", Count(page, "Alpha"));
        await Row(page, "Kart").QuerySelector("fluent-button[title=Delete]")!.ClickAsync(new());
        StringAssert.Contains(page.Confirmation().TextContent, "Kart has 1 activity.");
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    private IRenderedComponent<IComponent> Open(string route)
    {
        IRenderedComponent<IComponent> page = route switch
        {
            "games" => harness.Render<Games>(),
            "activities" => harness.Render<Activities>(),
            "themes" => harness.Render<Themes>(),
            "holidays" => harness.Render<Holidays>(),
            _ => harness.Render<Events>()
        };
        var lists = route switch { "events" => 5, "games" or "activities" => 4, _ => 1 };
        page.WaitForAssertion(() => Assert.AreEqual(lists, api.Requests.Count(r => r.Method == HttpMethod.Get)));
        page.WaitForRows(route switch { "events" => 5, "activities" => 4, _ => 3 });
        // The Activities grid shows each Game's name (not "—") once the Games list is in.
        if (route == "activities") page.WaitForAssertion(() => Assert.IsFalse(page.Rows().Any(r => r.Contains('—'))));
        return page;
    }

    // The page's own search box, never one in a dialog.
    private static IElement Search(IRenderedComponent<IComponent> page) =>
        page.FindAll("fluent-search").Single(s => s.Closest("fluent-dialog") is null);

    // The sort button in a sortable column's header.
    private static IElement SortHeader(IRenderedComponent<IComponent> page, string title) =>
        page.FindAll("th fluent-button.col-sort-button").Single(t => t.TextContent.Trim() == title);

    // The names, in the order the rows show them.
    private static string Order(IRenderedComponent<IComponent> page, IEnumerable<string> names) =>
        string.Join(", ", page.Rows().Select(r => names.Single(n => r.Contains(n))));

    private static IElement Row(IRenderedComponent<IComponent> page, string item) =>
        page.FindAll("tbody tr.fluent-data-grid-row").Where(r => r.Closest("fluent-dialog") is null).Single(r => r.TextContent.Contains(item));

    private static string Count(IRenderedComponent<IComponent> page, string game) =>
        Row(page, game).QuerySelector("fluent-badge")!.TextContent.Trim();
}
