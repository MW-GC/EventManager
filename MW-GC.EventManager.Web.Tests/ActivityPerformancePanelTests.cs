using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using Microsoft.FluentUI.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Pages;

namespace MW_GC.EventManager.Web.Tests;

// #137: Activity performance in the Activity details dialog, rendered with bUnit through the Activities page
// against a stub API. The panel loads the four lists the Stats page loads and shows its own Activity's row of
// the same computation, so its numbers are the Stats page's numbers. Nothing is stored (ADR 0001).
[TestClass]
public sealed partial class ActivityPerformancePanelTests : IDisposable
{
    private const string Sentence =
        "Performance cannot tell a weak Activity from a weak night: an Event has several Selections, so its numbers belong to all of them.";
    private const string Unreachable = "Could not reach the server. Check the connection and try again.";

    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly GameEntity kart = new() { Name = "Kart" };
    private readonly GameEntity puzzle = new() { Name = "Puzzle" };
    private readonly ActivityEntity race;
    private readonly ActivityEntity tiles;
    private readonly ActivityEntity unplayed;
    private readonly EventEntity[] events;
    private readonly EventMetricsWithLikes[] metrics;

    public ActivityPerformancePanelTests()
    {
        race = new ActivityEntity { Name = "Race", GameId = kart.Id };
        tiles = new ActivityEntity { Name = "Tiles", GameId = puzzle.Id };
        unplayed = new ActivityEntity { Name = "Unplayed", GameId = kart.Id };
        events =
        [
            // Race and Tiles both offered at two past Events; Race won the first, Tiles the second.
            Event(new DateTimeOffset(2020, 3, 1, 19, 0, 0, TimeSpan.Zero), race.Id, Pick(kart, race), Pick(puzzle, tiles)),
            Event(new DateTimeOffset(2020, 6, 1, 19, 0, 0, TimeSpan.Zero), tiles.Id, Pick(kart, race), Pick(puzzle, tiles)),
            // In the future: ignored, as on the Stats page.
            Event(new DateTimeOffset(2099, 1, 1, 19, 0, 0, TimeSpan.Zero), race.Id, Pick(kart, race)),
        ];
        metrics =
        [
            new(events[0].Id, 10, null, null, null, 2),
            new(events[1].Id, 20, null, null, null, 1),
            new(events[2].Id, 99, null, null, null, 9),
        ];
        api.Json(HttpMethod.Get, "/api/games", new[] { kart, puzzle })
           .Json(HttpMethod.Get, "/api/activities", new[] { race, tiles, unplayed })
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>())
           .Json(HttpMethod.Get, "/api/events", events)
           .Json(HttpMethod.Get, "/api/event-metrics", metrics);
        foreach (var a in new[] { race, tiles, unplayed })
            api.Json(HttpMethod.Get, $"/api/activities/{a.Id}/comments", Array.Empty<object>());
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    [TestMethod]
    public async Task AnActivityWithAppearances_ShowsOfferedWonWinRateLikesAndAttendees_InItsOwnPerformanceSection()
    {
        var page = Open();
        await OpenDetails(page, "Race");

        // 2 past Events, won 1; Likes (2 + 1) / 2; attendees (10 + 20) / 2. The future Event is left out.
        CollectionAssert.AreEqual(new[] { "Offered in 2 Events", "Won 1", "Win rate 50%", "Avg Likes 1.5", "Avg attendees 15.0" }, Values(page));
        var heading = Panel(page).QuerySelector("[aria-level='3']")!;
        Assert.AreEqual("Performance", heading.TextContent.Trim());
        Assert.IsNotNull(Panel(page).QuerySelector("dl"), "a definition list");
        Assert.IsNotNull(Panel(page).Closest("fluent-dialog"), "inside the details dialog");
        Assert.AreEqual(1, api.Count(HttpMethod.Get, "/api/event-metrics"));
        Assert.IsEmpty(page.FormDialog().Alerts());
    }

    [TestMethod]
    public async Task AnActivityWithNoAppearances_ShowsZeroZeroAndThreeDashes()
    {
        var page = Open();
        await OpenDetails(page, "Unplayed");

        CollectionAssert.AreEqual(new[] { "Offered in 0 Events", "Won 0", "Win rate —", "Avg Likes —", "Avg attendees —" }, Values(page));
    }

    [TestMethod]
    [DataRow("Race")]
    [DataRow("Tiles")]
    [DataRow("Unplayed")]
    public async Task TheNumbersEqualTheStatsRowForTheSameActivity(string name)
    {
        var stats = harness.Render<Stats>();
        stats.WaitForAssertion(() => Assert.HasCount(3, StatsRows(stats)));
        var row = StatsRows(stats).Single(r => r.QuerySelector(".stats-name")!.TextContent.Trim() == name);

        var page = Open();
        await OpenDetails(page, name);

        Assert.AreEqual(Text(row, ".stats-offered"), Text(Panel(page), ".performance-offered"));
        Assert.AreEqual(Text(row, ".stats-wins"), Text(Panel(page), ".performance-wins"));
        Assert.AreEqual(Text(row, ".stats-win-rate"), Text(Panel(page), ".performance-win-rate"));
        Assert.AreEqual(Text(row, ".stats-avg-likes"), Text(Panel(page), ".performance-avg-likes"));
        Assert.AreEqual(Text(row, ".stats-avg-attendees"), Text(Panel(page), ".performance-avg-attendees"));
    }

    [TestMethod]
    public async Task AnEventWithoutMetrics_CountsZeroLikes_AndIsLeftOutOfTheAttendees()
    {
        // The second Event has no metrics item at all: 0 Likes, and no attendees rather than 0.
        api.Json(HttpMethod.Get, "/api/event-metrics", new[] { metrics[0] });
        var page = Open();
        await OpenDetails(page, "Race");

        CollectionAssert.AreEqual(new[] { "Offered in 2 Events", "Won 1", "Win rate 50%", "Avg Likes 1.0", "Avg attendees 10.0" }, Values(page));
    }

    [TestMethod]
    public async Task AFailedEventMetricsList_SaysPerformanceCouldNotBeLoaded_AndRetryReloadsOnlyThatList()
    {
        api.Offline(HttpMethod.Get, "/api/event-metrics");
        var page = Open();
        await DetailsButton(page, "Race").ClickAsync(new());

        page.WaitForAssertion(() => Assert.Contains(
            $"Performance could not be loaded. Event metrics could not be loaded. {Unreachable}", Panel(page).TextContent));
        Assert.IsNull(Panel(page).QuerySelector("dl"));
        Assert.IsEmpty(page.PageAlerts(), "not the page's error bar");
        // The page itself also reads Games and Activities, so count what Retry adds.
        var before = new[] { "/api/events", "/api/games", "/api/activities" }.Select(p => api.Count(HttpMethod.Get, p)).ToList();
        Assert.AreEqual(1, before[0]);

        api.Json(HttpMethod.Get, "/api/event-metrics", metrics);
        await Panel(page).Button("Retry").ClickAsync(new());

        page.WaitForAssertion(() => Assert.HasCount(5, Values(page)));
        Assert.AreEqual("Avg Likes 1.5", Values(page)[3]);
        Assert.DoesNotContain("could not be loaded", Panel(page).TextContent);
        Assert.AreEqual(2, api.Count(HttpMethod.Get, "/api/event-metrics"));
        // The lists that loaded are not read again.
        CollectionAssert.AreEqual(before, new[] { "/api/events", "/api/games", "/api/activities" }.Select(p => api.Count(HttpMethod.Get, p)).ToList());
    }

    [TestMethod]
    public async Task ThePanelSaysPerformanceCannotTellAWeakActivityFromAWeakNight_AfterTheNumbers()
    {
        var page = Open();
        await OpenDetails(page, "Race");

        var text = Normalize(Panel(page).TextContent);
        Assert.Contains(Sentence, text);
        Assert.IsTrue(text.IndexOf("Avg attendees", StringComparison.Ordinal) < text.IndexOf(Sentence, StringComparison.Ordinal), "after the numbers");
    }

    [TestMethod]
    public async Task Loading_ShowsAProgressRingLabelledLoadingPerformance()
    {
        var gate = api.Hold(HttpMethod.Get, "/api/event-metrics", () => Ok(metrics));
        var page = Open();
        await DetailsButton(page, "Race").ClickAsync(new());

        page.WaitForAssertion(() => Assert.IsNotNull(Panel(page).QuerySelector("fluent-progress-ring[aria-label='Loading performance...']")));
        Assert.IsNull(Panel(page).QuerySelector("dl"));

        gate.SetResult();
        page.WaitForAssertion(() => Assert.HasCount(5, Values(page)));
        Assert.IsNull(Panel(page).QuerySelector("fluent-progress-ring"));
    }

    [TestMethod]
    [DataRow("close")]
    [DataRow("dismiss")]
    public async Task TheDialogClosesAtOnceWhilePerformanceLoads(string how)
    {
        var gate = api.Hold(HttpMethod.Get, "/api/event-metrics", () => Ok(metrics));
        var page = Open();
        await DetailsButton(page, "Race").ClickAsync(new());
        page.WaitForAssertion(() => Assert.IsNotNull(Panel(page).QuerySelector("fluent-progress-ring")));
        Assert.IsFalse(page.FormDialog().Button("Close").IsBusy());

        if (how == "close") await page.FormDialog().Button("Close").ClickAsync(new());
        else await Dismiss(page);

        Assert.IsFalse(page.HasFormDialog());
        Assert.IsFalse(page.HasConfirmation(), "no Discard question");
        gate.SetResult();
    }

    [TestMethod]
    public async Task ALateLoadAfterTheDialogClosed_DoesNotShowInTheNextActivity()
    {
        var gate = api.Hold(HttpMethod.Get, "/api/event-metrics", () => Ok(metrics));
        var page = Open();
        await DetailsButton(page, "Race").ClickAsync(new());
        page.WaitForAssertion(() => Assert.IsNotNull(Panel(page).QuerySelector("fluent-progress-ring")));
        await page.FormDialog().Button("Close").ClickAsync(new());

        await DetailsButton(page, "Unplayed").ClickAsync(new());
        gate.SetResult();

        page.WaitForAssertion(() => Assert.HasCount(5, Values(page)));
        Assert.AreEqual("Unplayed", page.FormDialog().GetAttribute("aria-label"));
        CollectionAssert.AreEqual(new[] { "Offered in 0 Events", "Won 0", "Win rate —", "Avg Likes —", "Avg attendees —" }, Values(page));
        Assert.HasCount(1, page.FindAll(".activity-performance"));
        Assert.IsFalse(harness.UnhandledException.IsCompleted);
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static Selection Pick(GameEntity game, ActivityEntity activity) => new()
    {
        Game = new Game { Id = game.Id, Name = game.Name },
        Activity = new Activity { Id = activity.Id, GameId = game.Id, Name = activity.Name }
    };

    private static EventEntity Event(DateTimeOffset date, Guid? winner, params Selection[] selections) => new()
    {
        Name = "Night " + date.ToString("yyyy-MM-dd"),
        Date = date,
        UniqueGamesOnly = false,
        Selections = [.. selections],
        WinnerActivityId = winner
    };

    private static HttpResponseMessage Ok<T>(T body) =>
        new(System.Net.HttpStatusCode.OK) { Content = System.Net.Http.Json.JsonContent.Create(body) };

    // Renders the Activities page and waits for its rows with their Game names.
    private IRenderedComponent<Activities> Open()
    {
        var page = harness.Render<Activities>();
        page.WaitForRows(3);
        page.WaitForAssertion(() => Assert.IsFalse(page.Rows().Any(r => r.Contains('—'))));
        return page;
    }

    // Opens one Activity's details and waits for its performance numbers.
    private static async Task OpenDetails(IRenderedComponent<Activities> page, string name)
    {
        await DetailsButton(page, name).ClickAsync(new());
        page.WaitForAssertion(() => Assert.HasCount(5, Values(page)));
    }

    private static IElement DetailsButton(IRenderedComponent<Activities> page, string name) =>
        page.FindAll("tbody tr.fluent-data-grid-row").Single(r => r.Closest("fluent-dialog") is null && r.TextContent.Contains(name))
            .QuerySelector("fluent-button[title=Details]")!;

    private static IElement Panel(IRenderedComponent<Activities> page) => page.FormDialog().QuerySelector(".activity-performance")!;

    // Each labelled value of the definition list, as read: the term, a space, then its value.
    private static List<string> Values(IRenderedComponent<Activities> page) =>
        page.FormDialog().QuerySelectorAll(".activity-performance dl > div")
            .Select(d => string.Join(" ", d.Children.Select(c => Normalize(c.TextContent)))).ToList();

    private static List<IElement> StatsRows(IRenderedComponent<Stats> page) =>
        page.FindAll("table.stats-activities tbody tr.fluent-data-grid-row").Where(r => r.QuerySelector(".stats-name") is not null).ToList();

    private static string Text(IElement root, string selector) => Normalize(root.QuerySelector(selector)!.TextContent);

    private static string Normalize(string text) => Spaces().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // Overlay click or Escape on the details dialog.
    private static Task Dismiss(IRenderedComponent<Activities> page)
    {
        var dialog = page.FormDialog();
        return dialog.TriggerEventAsync("ondialogdismiss", new DialogEventArgs { Id = dialog.Id, Reason = "dismiss" });
    }
}
