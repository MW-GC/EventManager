using System.Net;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Pages;

namespace MW_GC.EventManager.Web.Tests;

// Issue #96: the Stats page rendered with bUnit against a stub API. It loads the Events, Games and
// Activities lists, shows one grid per kind in the default order (never offered first, then the longest
// unused), marks deleted items with visible text, and shows a failed load with Retry like the other pages.
[TestClass]
public sealed class StatsPageTests : IDisposable
{
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly GameEntity kart = new() { Name = "Kart" };
    private readonly GameEntity puzzle = new() { Name = "Puzzle" };
    private readonly GameEntity unused = new() { Name = "Unused" };
    private readonly ActivityEntity sprint;
    private readonly ActivityEntity tiles;
    private readonly Guid goneGame = Guid.NewGuid();
    private readonly Guid goneActivity = Guid.NewGuid();
    private readonly EventEntity[] events;
    private readonly EventMetricsWithLikes[] metrics;

    public StatsPageTests()
    {
        sprint = new ActivityEntity { Name = "Sprint", GameId = kart.Id };
        tiles = new ActivityEntity { Name = "Tiles", GameId = puzzle.Id };
        events =
        [
            Event(new DateTimeOffset(2020, 3, 1, 19, 0, 0, TimeSpan.Zero), sprint.Id, Pick(kart.Id, kart.Name, sprint.Id, sprint.Name), Pick(puzzle.Id, puzzle.Name, tiles.Id, tiles.Name)),
            Event(new DateTimeOffset(2020, 6, 1, 19, 0, 0, TimeSpan.Zero), sprint.Id, Pick(kart.Id, kart.Name, sprint.Id, sprint.Name)),
            Event(new DateTimeOffset(2019, 1, 1, 19, 0, 0, TimeSpan.Zero), goneActivity, Pick(goneGame, "Old game", goneActivity, "Old activity")),
            // In the future: ignored.
            Event(new DateTimeOffset(2099, 1, 1, 19, 0, 0, TimeSpan.Zero), tiles.Id, Pick(puzzle.Id, puzzle.Name, tiles.Id, tiles.Name)),
        ];
        // Event metrics with Like counts (#136): Sprint is at the first two Events, Tiles at the first, Old
        // activity at the third. The future Event's and an unknown Event's items must not count.
        metrics =
        [
            new(events[0].Id, 10, null, null, null, 4),
            new(events[1].Id, 20, null, null, null, 0),
            new(events[2].Id, null, null, null, null, 1),
            new(events[3].Id, 99, null, null, null, 9),
            new(Guid.NewGuid(), 50, null, null, null, 50),
        ];
        api.Json(HttpMethod.Get, "/api/games", new[] { kart, puzzle, unused })
           .Json(HttpMethod.Get, "/api/activities", new[] { sprint, tiles })
           .Json(HttpMethod.Get, "/api/events", events)
           .Json(HttpMethod.Get, "/api/event-metrics", metrics);
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    private static Selection Pick(Guid gameId, string gameName, Guid activityId, string activityName) => new()
    {
        Game = new Game { Id = gameId, Name = gameName },
        Activity = new Activity { Id = activityId, GameId = gameId, Name = activityName }
    };

    private static EventEntity Event(DateTimeOffset date, Guid? winner, params Selection[] selections) => new()
    {
        Name = "Night " + date.ToString("yyyy-MM-dd"),
        Date = date,
        UniqueGamesOnly = false,
        Selections = [.. selections],
        WinnerActivityId = winner
    };

    [TestMethod]
    public void GamesGrid_ShowsEveryGame_InTheDefaultOrder_WithItsMeasures()
    {
        var page = Loaded();

        var rows = Rows(page, "stats-games");
        CollectionAssert.AreEqual(new[] { "Unused", "Old game", "Puzzle", "Kart" }, rows.Select(Name).ToList());
        Assert.AreEqual(("0", "0", "Never"), Measures(rows[0]));
        Assert.AreEqual(("1", "1"), (Measures(rows[1]).Offered, Measures(rows[1]).Wins));
        Assert.AreEqual(("1", "0"), (Measures(rows[2]).Offered, Measures(rows[2]).Wins));
        Assert.AreEqual(("2", "2"), (Measures(rows[3]).Offered, Measures(rows[3]).Wins));
        Assert.AreEqual(new DateTimeOffset(2020, 6, 1, 19, 0, 0, TimeSpan.Zero).ToString("O"),
            rows[3].QuerySelector(".stats-last time")!.GetAttribute("datetime"));
        Assert.IsEmpty(page.PageAlerts());
        Assert.AreEqual(1, api.Count(HttpMethod.Get, "/api/events"));
        Assert.AreEqual(1, api.Count(HttpMethod.Get, "/api/games"));
        Assert.AreEqual(1, api.Count(HttpMethod.Get, "/api/activities"));
    }

    [TestMethod]
    public void ActivitiesGrid_ShowsEveryActivity_WithItsGame()
    {
        var page = Loaded();

        var rows = Rows(page, "stats-activities");
        CollectionAssert.AreEqual(new[] { "Old activity", "Tiles", "Sprint" }, rows.Select(Name).ToList());
        CollectionAssert.AreEqual(new[] { "Old game", "Puzzle", "Kart" },
            rows.Select(r => r.QuerySelector(".stats-game")!.TextContent.Trim()).ToList());
        Assert.AreEqual(("2", "2"), (Measures(rows[2]).Offered, Measures(rows[2]).Wins));
        Assert.AreEqual(("1", "0"), (Measures(rows[1]).Offered, Measures(rows[1]).Wins));
    }

    [TestMethod]
    public void DeletedItems_CarryAVisibleDeletedMarker_AndOthersDoNot()
    {
        var page = Loaded();

        foreach (var (grid, name) in new[] { ("stats-games", "Old game"), ("stats-activities", "Old activity") })
        {
            foreach (var row in Rows(page, grid))
            {
                var marker = row.QuerySelector(".stats-deleted");
                if (Name(row) == name)
                {
                    Assert.IsNotNull(marker, name);
                    Assert.AreEqual("Deleted", marker.TextContent.Trim());
                    Assert.IsFalse(string.IsNullOrEmpty(marker.GetAttribute("title")));
                }
                else Assert.IsNull(marker, Name(row));
            }
        }
    }

    [TestMethod]
    public void Grids_HeadingsAndSortHeaders_AreNamed()
    {
        var page = Loaded();

        Assert.AreEqual("Game stats", page.Find("table.stats-games").GetAttribute("aria-label"));
        Assert.AreEqual("Activity stats", page.Find("table.stats-activities").GetAttribute("aria-label"));
        var h1 = page.FindAll("h1").Single();
        Assert.AreEqual("Stats", h1.TextContent.Trim());
        Assert.AreEqual("-1", h1.GetAttribute("tabindex"));
        CollectionAssert.AreEqual(new[] { "Name", "Offered", "Wins", "Last offered" }, SortHeaders(page, "stats-games"));
        CollectionAssert.AreEqual(new[] { "Name", "Game", "Offered", "Wins", "Win rate", "Avg Likes", "Avg attendees", "Last offered" }, SortHeaders(page, "stats-activities"));
        // The default sort is announced on its header.
        Assert.AreEqual("ascending", page.Find("table.stats-games th[aria-sort]:not([aria-sort=none])").GetAttribute("aria-sort"));
    }

    [TestMethod]
    public async Task ClickingASortHeader_ReSorts()
    {
        var page = Loaded();

        await SortHeader(page, "stats-games", "Name").ClickAsync(new MouseEventArgs());

        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { "Kart", "Old game", "Puzzle", "Unused" },
            Rows(page, "stats-games").Select(Name).ToList()));
    }

    [TestMethod]
    public void AnEmptyLibrary_ShowsBothGridsWithNoRows_AndNoError()
    {
        api.Json(HttpMethod.Get, "/api/games", Array.Empty<GameEntity>())
           .Json(HttpMethod.Get, "/api/activities", Array.Empty<ActivityEntity>())
           .Json(HttpMethod.Get, "/api/events", Array.Empty<EventEntity>());

        var page = harness.Render<Stats>();
        page.WaitForAssertion(() => Assert.HasCount(2, page.FindAll("table.fluent-data-grid")));

        Assert.IsEmpty(Rows(page, "stats-games"));
        Assert.IsEmpty(Rows(page, "stats-activities"));
        Assert.IsEmpty(page.PageAlerts());
    }

    [TestMethod]
    public async Task FailedLoad_ShowsTheMessageNotTheSpinner_AndRetryShowsTheStats()
    {
        api.Offline(HttpMethod.Get, "/api/events");

        var page = harness.Render<Stats>();
        page.WaitForPageAlert();

        Assert.AreEqual("Events could not be loaded. Could not reach the server. Check the connection and try again.", Assert.ContainsSingle(page.PageAlerts()));
        Assert.DoesNotContain("Loading stats...", page.Markup);
        Assert.IsEmpty(page.FindAll("fluent-progress-ring"));
        Assert.IsEmpty(page.FindAll("table.fluent-data-grid"));

        api.Json(HttpMethod.Get, "/api/events", events);
        await page.Button("Retry").ClickAsync(new());

        page.WaitForAssertion(() => Assert.HasCount(4, Rows(page, "stats-games")));
        Assert.IsEmpty(page.PageAlerts());
    }

    [TestMethod]
    public void AFailedLibraryList_IsNamed_AndNoStatsAreShownThatWouldMarkEverythingDeleted()
    {
        api.Status(HttpMethod.Get, "/api/games", HttpStatusCode.InternalServerError);

        var page = harness.Render<Stats>();
        page.WaitForPageAlert();

        Assert.AreEqual("Games could not be loaded. The server had a problem (HTTP 500). Try again.", Assert.ContainsSingle(page.PageAlerts()));
        Assert.IsEmpty(page.FindAll("table.fluent-data-grid"));
        Assert.DoesNotContain("Deleted", page.Markup);
    }

    // ---- Activity performance (#136) ------------------------------------------------------------

    private const string PerformanceNote =
        "Performance cannot tell a weak Activity from a weak night: an Event has several Selections, so its numbers belong to all of them.";

    [TestMethod]
    public void ActivitiesGrid_ShowsWinRateAverageLikesAndAverageAttendees()
    {
        var page = Loaded();

        var rows = Rows(page, "stats-activities");
        CollectionAssert.AreEqual(new[] { "Old activity", "Tiles", "Sprint" }, rows.Select(Name).ToList());
        // Sprint: 2 of 2 won, Likes (4 + 0) / 2, attendees (10 + 20) / 2. Tiles: 0 of 1 won, the first Event's 4
        // Likes and 10 attendees. Old activity: its Event has Likes but no attendees.
        CollectionAssert.AreEqual(new[] { "100%", "1.0", "—" }, PerformanceCells(rows[0]));
        CollectionAssert.AreEqual(new[] { "0%", "4.0", "10.0" }, PerformanceCells(rows[1]));
        CollectionAssert.AreEqual(new[] { "100%", "2.0", "15.0" }, PerformanceCells(rows[2]));
        Assert.AreEqual(1, api.Count(HttpMethod.Get, "/api/event-metrics"));
        Assert.IsEmpty(page.FindAll("table.stats-games .stats-win-rate, table.stats-games .stats-avg-likes, table.stats-games .stats-avg-attendees"),
            "Games get no performance columns");
    }

    [TestMethod]
    public void PerformanceNumbers_ShowAWholePercentAndOneDecimal()
    {
        var nights = new[]
        {
            Event(new DateTimeOffset(2021, 1, 1, 19, 0, 0, TimeSpan.Zero), sprint.Id, Pick(kart.Id, kart.Name, sprint.Id, sprint.Name)),
            Event(new DateTimeOffset(2021, 2, 1, 19, 0, 0, TimeSpan.Zero), sprint.Id, Pick(kart.Id, kart.Name, sprint.Id, sprint.Name)),
            Event(new DateTimeOffset(2021, 3, 1, 19, 0, 0, TimeSpan.Zero), null, Pick(kart.Id, kart.Name, sprint.Id, sprint.Name)),
        };
        api.Json(HttpMethod.Get, "/api/events", nights)
           .Json(HttpMethod.Get, "/api/event-metrics", new EventMetricsWithLikes[]
           {
               new(nights[0].Id, 10, null, null, null, 1),
               new(nights[1].Id, 11, null, null, null, 0),
               new(nights[2].Id, 11, null, null, null, 0),
           });

        var page = harness.Render<Stats>();
        page.WaitForAssertion(() => Assert.HasCount(2, Rows(page, "stats-activities")));

        var row = Rows(page, "stats-activities").Single(r => Name(r) == "Sprint");
        // 2/3 won, 1/3 Likes, 32/3 attendees.
        CollectionAssert.AreEqual(new[] { "67%", "0.3", "10.7" }, PerformanceCells(row));
    }

    [TestMethod]
    public void AnActivityNeverOffered_ShowsAnEmDashInEachPerformanceColumn()
    {
        var unplayed = new ActivityEntity { Name = "Unplayed", GameId = kart.Id };
        api.Json(HttpMethod.Get, "/api/activities", new[] { sprint, tiles, unplayed });

        var page = harness.Render<Stats>();
        page.WaitForAssertion(() => Assert.HasCount(4, Rows(page, "stats-activities")));

        var row = Rows(page, "stats-activities").Single(r => Name(r) == "Unplayed");
        CollectionAssert.AreEqual(new[] { "—", "—", "—" }, PerformanceCells(row));
        Assert.AreEqual(("0", "0", "Never"), Measures(row));
    }

    [TestMethod]
    [DataRow("Win rate", new[] { "Unplayed", "Tiles", "Old activity", "Sprint" })]
    [DataRow("Avg Likes", new[] { "Unplayed", "Old activity", "Sprint", "Tiles" })]
    [DataRow("Avg attendees", new[] { "Old activity", "Unplayed", "Tiles", "Sprint" })]
    public async Task ClickingAPerformanceHeader_SortsAscendingWithBlanksFirst_ThenDescending(string header, string[] ascending)
    {
        var unplayed = new ActivityEntity { Name = "Unplayed", GameId = kart.Id };
        api.Json(HttpMethod.Get, "/api/activities", new[] { sprint, tiles, unplayed });
        var page = harness.Render<Stats>();
        page.WaitForAssertion(() => Assert.HasCount(4, Rows(page, "stats-activities")));
        CollectionAssert.AreEqual(new[] { "Unplayed", "Old activity", "Tiles", "Sprint" }, Rows(page, "stats-activities").Select(Name).ToList(),
            "the default sort stays Last offered");

        await SortHeader(page, "stats-activities", header).ClickAsync(new MouseEventArgs());
        page.WaitForAssertion(() => CollectionAssert.AreEqual(ascending, Rows(page, "stats-activities").Select(Name).ToList()));

        await SortHeader(page, "stats-activities", header).ClickAsync(new MouseEventArgs());
        page.WaitForAssertion(() => CollectionAssert.AreEqual(ascending.Reverse().ToArray(), Rows(page, "stats-activities").Select(Name).ToList()));
    }

    [TestMethod]
    public void TheActivitiesCard_SaysPerformanceCannotTellAWeakActivityFromAWeakNight()
    {
        var page = Loaded();

        var note = page.Find(".stats-performance-note");
        Assert.AreEqual(PerformanceNote, note.TextContent.Trim());
        Assert.AreEqual(1, page.Markup.Split(PerformanceNote).Length - 1, "said once");
        var card = note.Closest("fluent-card")!;
        Assert.IsNotNull(card.QuerySelector("table.stats-activities"), "under the Activities heading");
        Assert.IsNull(card.QuerySelector("table.stats-games"));
    }

    [TestMethod]
    public async Task AFailedEventMetricsLoad_IsNamed_ShowsNoStats_AndRetryShowsThem()
    {
        api.Offline(HttpMethod.Get, "/api/event-metrics");

        var page = harness.Render<Stats>();
        page.WaitForPageAlert();

        Assert.AreEqual("Event metrics could not be loaded. Could not reach the server. Check the connection and try again.",
            Assert.ContainsSingle(page.PageAlerts()));
        Assert.IsEmpty(page.FindAll("table.fluent-data-grid"), "the stats wait for all four lists");

        api.Json(HttpMethod.Get, "/api/event-metrics", metrics);
        await page.Button("Retry").ClickAsync(new());

        page.WaitForAssertion(() => Assert.HasCount(3, Rows(page, "stats-activities")));
        Assert.IsEmpty(page.PageAlerts());
        CollectionAssert.AreEqual(new[] { "100%", "2.0", "15.0" }, PerformanceCells(Rows(page, "stats-activities").Single(r => Name(r) == "Sprint")));
    }

    // ---- Helpers -------------------------------------------------------------------------------

    // The win rate, average Likes and average attendees cells of one row, as read.
    private static string[] PerformanceCells(IElement row) =>
        new[] { ".stats-win-rate", ".stats-avg-likes", ".stats-avg-attendees" }.Select(c => row.QuerySelector(c)!.TextContent.Trim()).ToArray();

    private IRenderedComponent<Stats> Loaded()
    {
        var page = harness.Render<Stats>();
        page.WaitForAssertion(() => Assert.HasCount(4, Rows(page, "stats-games")));
        return page;
    }

    // Item rows of one grid (an empty grid's placeholder row is not an item).
    private static List<IElement> Rows(IRenderedComponent<Stats> page, string grid) =>
        page.FindAll($"table.{grid} tbody tr.fluent-data-grid-row").Where(r => r.QuerySelector(".stats-name") is not null).ToList();

    private static string Name(IElement row) => row.QuerySelector(".stats-name")!.TextContent.Trim();

    private static (string Offered, string Wins, string Last) Measures(IElement row) => (
        row.QuerySelector(".stats-offered")!.TextContent.Trim(),
        row.QuerySelector(".stats-wins")!.TextContent.Trim(),
        row.QuerySelector(".stats-last")!.TextContent.Trim());

    private static List<string> SortHeaders(IRenderedComponent<Stats> page, string grid) =>
        page.FindAll($"table.{grid} th fluent-button.col-sort-button").Select(b => b.TextContent.Trim()).ToList();

    private static IElement SortHeader(IRenderedComponent<Stats> page, string grid, string title) =>
        page.FindAll($"table.{grid} th fluent-button.col-sort-button").Single(b => b.TextContent.Trim() == title);
}
