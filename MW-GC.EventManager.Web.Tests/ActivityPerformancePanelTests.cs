using System.Net;
using AngleSharp.Dom;
using Bunit;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Pages;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// #137: Activity performance in the Activity details dialog, rendered with bUnit through the Activities page
// against a stub API. The panel loads the four lists the Stats page loads and shows its own Activity's row of
// the same computation, so its numbers are the Stats page's numbers. Nothing is stored (ADR 0001).
[TestClass]
public sealed class ActivityPerformancePanelTests : IDisposable
{
    private const string Sentence =
        "Performance cannot tell a weak Activity from a weak night: an Event has several Selections, so its numbers belong to all of them.";
    private const string NoPerformance = "No performance to show for this Activity.";
    private const string Unreachable = "Could not reach the server. Check the connection and try again.";
    private const string MetricsPath = "/api/event-metrics";
    // The five numbers' classes, in the order the Stats row (stats-…) and the panel (performance-…) show them.
    private static readonly string[] Numbers = ["offered", "wins", "win-rate", "avg-likes", "avg-attendees"];

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
            // Race offered at two past Events and won the first; Tiles offered only at the first, so the two
            // Activities differ in every number.
            Event(new DateTimeOffset(2020, 3, 1, 19, 0, 0, TimeSpan.Zero), race.Id, Pick(kart, race), Pick(puzzle, tiles)),
            Event(new DateTimeOffset(2020, 6, 1, 19, 0, 0, TimeSpan.Zero), null, Pick(kart, race)),
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
    public async Task AnActivityOffered_ShowsOfferedWonWinRateLikesAndAttendees_InItsOwnPerformanceSection()
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
    public async Task AnActivityNeverOffered_ShowsZeroZeroAndThreeDashes()
    {
        var page = Open();
        await OpenDetails(page, "Unplayed");

        CollectionAssert.AreEqual(new[] { "Offered in 0 Events", "Won 0", "Win rate —", "Avg Likes —", "Avg attendees —" }, Values(page));
    }

    [TestMethod]
    public async Task AnActivityOfferedInOneEvent_SaysOneEvent()
    {
        var page = Open();
        await OpenDetails(page, "Tiles");

        Assert.AreEqual("Offered in 1 Event", Values(page)[0]);
    }

    // #182.7: Race and Tiles differ in every number, and both the Stats row and the panel are compared with the
    // numbers worked out by hand, so a panel showing another Activity's row fails.
    [TestMethod]
    [DataRow("Race", "2", "1", "50%", "1.5", "15.0")]
    [DataRow("Tiles", "1", "0", "0%", "2.0", "10.0")]
    [DataRow("Unplayed", "0", "0", "—", "—", "—")]
    public async Task TheNumbersEqualTheStatsRowForTheSameActivity(string name, string offered, string wins, string winRate, string likes, string attendees)
    {
        var expected = new[] { offered, wins, winRate, likes, attendees };
        var stats = harness.Render<Stats>();
        stats.WaitForAssertion(() => Assert.HasCount(3, StatsRows(stats)));
        var row = StatsRows(stats).Single(r => r.QuerySelector(".stats-name")!.TextContent.Trim() == name);
        CollectionAssert.AreEqual(expected, Numbers.Select(c => Text(row, ".stats-" + c)).ToArray(), "the Stats row");

        var page = Open();
        await OpenDetails(page, name);

        CollectionAssert.AreEqual(expected, Numbers.Select(c => Text(Panel(page), ".performance-" + c)).ToArray(), "the panel");
    }

    // #182.2: an Activity in neither the Library the panel read nor any past Selection (deleted after the page
    // loaded, never offered) has no row to show: one line, not a made-up "never offered" row marked deleted.
    [TestMethod]
    public async Task AnActivityTheComputationDoesNotKnow_ShowsNoPerformanceRows_AndSaysSo()
    {
        var page = Open();
        api.Json(HttpMethod.Get, "/api/activities", new[] { race, tiles });

        await DetailsButton(page, "Unplayed").ClickAsync(new());

        page.WaitForAssertion(() => Assert.Contains(NoPerformance, Normalize(Panel(page).TextContent)));
        // Nothing but the heading and that line: no Offered, Won or dash.
        var text = Normalize(Panel(page).TextContent);
        Assert.StartsWith("Performance", text);
        Assert.AreEqual(NoPerformance, text["Performance".Length..].Trim());
        Assert.IsNull(Panel(page).QuerySelector("dl"));
        Assert.IsNull(Panel(page).QuerySelector("fluent-progress-ring"));
        Assert.IsEmpty(page.FormDialog().Alerts());
    }

    // #182.8: the dialog stays open and its Activity changes. The first Activity's metrics read is still held when
    // the second Activity's panel loads; when it ends, it must not land in the panel now shown. Without the key on
    // the panel the same panel takes the new Activity and keeps its held read: no second metrics read is sent and
    // Tiles' numbers never show. #198.3: every wait is on something the test can see (the second metrics read, the
    // numbers, the held read answered), never a fixed delay.
    [TestMethod]
    public async Task SwitchingTheActivityWhileTheDialogStaysOpen_IgnoresTheFirstActivitysLateLoad()
    {
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = api.Hold(HttpMethod.Get, MetricsPath, () =>
        {
            var late = Ok(metrics);
            answered.SetResult();
            return late;
        });
        var page = Open();
        await DetailsButton(page, "Race").ClickAsync(new());
        page.WaitForAssertion(() => Assert.IsNotNull(Panel(page).QuerySelector("fluent-progress-ring")));
        Assert.AreEqual(1, api.Count(HttpMethod.Get, MetricsPath));

        // Tiles' only Event now has 6 Likes and 30 attendees.
        api.Json(HttpMethod.Get, MetricsPath, new EventMetricsWithLikes[] { new(events[0].Id, 30, null, null, null, 6) });
        await DetailsButton(page, "Tiles").ClickAsync(new());
        Assert.IsTrue(page.HasFormDialog(), "the dialog stayed open");
        // A new panel for Tiles sends its own metrics read while Race's is still held.
        page.WaitForAssertion(() => Assert.AreEqual(2, api.Count(HttpMethod.Get, MetricsPath), "Tiles' own metrics read"));
        var tiles = new[] { "Offered in 1 Event", "Won 0", "Win rate 0%", "Avg Likes 6.0", "Avg attendees 30.0" };
        page.WaitForAssertion(() => CollectionAssert.AreEqual(tiles, Values(page)));

        gate.SetResult();
        await answered.Task;

        page.WaitForAssertion(() => CollectionAssert.AreEqual(tiles, Values(page)));
        Assert.AreEqual(2, api.Count(HttpMethod.Get, MetricsPath), "no third read");
        Assert.AreEqual("Tiles", page.FormDialog().GetAttribute("aria-label"));
        Assert.HasCount(1, page.FindAll(".activity-performance"));
        Assert.IsFalse(harness.UnhandledException.IsCompleted);
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

    private static EventEntity Event(DateTimeOffset date, Guid? winner, params Selection[] selections) => new()
    {
        Name = "Night " + date.ToString("yyyy-MM-dd"),
        Date = date,
        UniqueGamesOnly = false,
        Selections = [.. selections],
        WinnerActivityId = winner
    };

    private static HttpResponseMessage Ok<T>(T body) =>
        new(HttpStatusCode.OK) { Content = System.Net.Http.Json.JsonContent.Create(body) };

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

    private static IElement DetailsButton(IRenderedComponent<Activities> page, string name) => RowButton(page, name, "Details");

    private static IElement Panel(IRenderedComponent<Activities> page) => page.FormDialog().QuerySelector(".activity-performance")!;

    // Each labelled value of the definition list, as read: the term, a space, then its value.
    private static List<string> Values(IRenderedComponent<Activities> page) =>
        page.FormDialog().QuerySelectorAll(".activity-performance dl > div")
            .Select(d => string.Join(" ", d.Children.Select(c => Normalize(c.TextContent)))).ToList();

    private static List<IElement> StatsRows(IRenderedComponent<Stats> page) =>
        page.FindAll("table.stats-activities tbody tr.fluent-data-grid-row").Where(r => r.QuerySelector(".stats-name") is not null).ToList();

    private static string Text(IElement root, string selector) => Normalize(root.QuerySelector(selector)!.TextContent);
}
