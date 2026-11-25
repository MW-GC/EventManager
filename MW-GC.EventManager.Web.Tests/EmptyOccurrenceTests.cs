using Bunit;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Pages;
using MW_GC.EventManager.Web.Services;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// #100, #204: the repeat marker in the Events list, and every Web reader of an Event's Selections with an Occurrence
// that has none yet (ADR 0002). "Weekly night" is an Occurrence (SeriesId set, no Selections); "Game night" is a
// standalone Event with one Selection, its Winner.
[TestClass]
public sealed class EmptyOccurrenceTests : IDisposable
{
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly GameEntity kart = new() { Name = "Kart" };
    private readonly ActivityEntity grandPrix;
    private readonly EventEntity night;
    private readonly EventEntity weekly;

    public EmptyOccurrenceTests()
    {
        grandPrix = new ActivityEntity { Name = "Grand Prix", GameId = kart.Id };
        night = new EventEntity { Name = "Game night", Date = new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero), Selections = [Pick(kart, grandPrix)], WinnerActivityId = grandPrix.Id };
        weekly = new EventEntity { Name = "Weekly night", Date = new DateTimeOffset(2026, 10, 2, 19, 0, 0, TimeSpan.Zero), SeriesId = Guid.NewGuid(), OccurrenceKey = new DateTimeOffset(2026, 10, 2, 19, 0, 0, TimeSpan.Zero) };
        api.Json(HttpMethod.Get, "/api/events", new[] { night, weekly })
           .Json(HttpMethod.Get, "/api/games", new[] { kart })
           .Json(HttpMethod.Get, "/api/activities", new[] { grandPrix })
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>())
           .Json(HttpMethod.Get, $"/api/events/{weekly.Id}/metrics", new { id = weekly.Id })
           .Json(HttpMethod.Get, $"/api/events/{weekly.Id}/likes", new { count = 0 });
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    // The marker has an accessible name on the Occurrence's row and is missing from the standalone Event's row; the
    // empty Occurrence shows 0 Selections and no Winner, and the grid keeps its five columns.
    [TestMethod]
    public void TheList_MarksTheOccurrenceOnly_AndShowsZeroSelectionsAndNoWinner()
    {
        var page = Loaded();

        var occurrence = Row(page, "Weekly night");
        var marker = Assert.ContainsSingle(occurrence.QuerySelectorAll(".repeat-marker"));
        Assert.AreEqual("Repeats", marker.GetAttribute("aria-label"));
        Assert.AreEqual("img", marker.GetAttribute("role"));
        Assert.AreEqual("Repeats", marker.GetAttribute("title"), "the marker's hover text");
        Assert.IsEmpty(Row(page, "Game night").QuerySelectorAll(".repeat-marker"));

        var cells = occurrence.QuerySelectorAll("td").Select(c => Normalize(c.TextContent)).ToList();
        Assert.HasCount(5, cells);
        Assert.AreEqual("Weekly night", cells[0]);
        Assert.AreEqual("0", cells[2]);
        Assert.AreEqual("None", cells[3]);
        Assert.AreEqual("minmax(0, 1fr) minmax(0, 1fr) minmax(0, 1fr) minmax(0, 1fr) 120px",
            page.Find("table").GetAttribute("style")!.Split("grid-template-columns:")[1].Split(';')[0].Trim());
    }

    // Details of an empty Occurrence opens with no error and no Selection card, so no Select Winner either.
    [TestMethod]
    public async Task Details_OfAnEmptyOccurrence_OpensWithoutError_AndOffersNoWinner()
    {
        var page = Loaded();

        await RowButton(page, "Weekly night", "Details").ClickAsync(new());

        Assert.IsTrue(page.HasFormDialog());
        Assert.AreEqual("Weekly night", page.FormDialog().GetAttribute("aria-label"));
        Assert.IsEmpty(page.FormDialog().Alerts());
        Assert.IsFalse(page.FormDialog().QuerySelectorAll("fluent-button").Any(b => b.TextContent.Trim() == "Select Winner"));
        Assert.IsFalse(harness.UnhandledException.IsCompleted);
    }

    // Edit of an empty Occurrence opens with no slot and no error and no Repeat section; rolling it is the next card.
    [TestMethod]
    public async Task Edit_OfAnEmptyOccurrence_OpensWithoutError()
    {
        var page = Loaded();

        await RowButton(page, "Weekly night", "Edit").ClickAsync(new());

        Assert.AreEqual("Edit Event", page.DialogTitle());
        Assert.IsEmpty(page.FormDialog().Alerts());
        Assert.IsEmpty(page.FormDialog().QuerySelectorAll("fluent-button[aria-label^='Re-roll slot']"));
        Assert.IsNull(page.FormDialog().QuerySelector(".repeat-section"));
        Assert.IsFalse(harness.UnhandledException.IsCompleted);
    }

    // Stats, the Activity performance panel and Play stats all run PlayStats.Compute: an empty past Occurrence offered
    // nothing, so it adds no Offered, no win and no Likes to any row, and no rate is a division by zero.
    [TestMethod]
    public void PlayStats_CountsNothingForAnEmptyOccurrence()
    {
        var metrics = new[] { new EventMetricsWithLikes(weekly.Id, 9, null, null, null, 4) };
        var stats = new PlayStats(new FixedClock(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)));

        var withIt = stats.Compute([night, weekly], [kart], [grandPrix], metrics);
        var onlyIt = stats.Compute([weekly], [kart], [grandPrix], metrics);

        var row = Assert.ContainsSingle(withIt.Activities);
        Assert.AreEqual((1, 1, night.Date), (row.Offered, row.Wins, row.LastOffered));
        Assert.AreEqual(new ActivityPerformance(1m, 0m, null), row.Performance);
        var unused = Assert.ContainsSingle(onlyIt.Activities);
        Assert.AreEqual((0, 0, (DateTimeOffset?)null), (unused.Offered, unused.Wins, unused.LastOffered));
        Assert.AreEqual(new ActivityPerformance(null, null, null), unused.Performance);
        Assert.AreEqual(0, Assert.ContainsSingle(onlyIt.Games).Offered);
    }

    // The Stats page renders with an empty Occurrence in the list: no "NaN", the Activity's numbers from the one Event.
    [TestMethod]
    public void TheStatsPage_RendersWithAnEmptyOccurrence()
    {
        api.Json(HttpMethod.Get, "/api/event-metrics", Array.Empty<EventMetricsWithLikes>());
        var page = harness.Render<Stats>();

        page.WaitForAssertion(() => Assert.HasCount(2, page.FindAll("tbody tr.fluent-data-grid-row")));
        Assert.DoesNotContain("NaN", page.Markup);
        Assert.AreEqual("1", page.Find("table.stats-activities .stats-offered").TextContent.Trim());
        Assert.IsEmpty(page.PageAlerts());
    }

    // ---- helpers -------------------------------------------------------------------------------

    private IRenderedComponent<Events> Loaded()
    {
        var page = harness.Render<Events>();
        page.WaitForRows(2);
        return page;
    }

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<Events> page, string name) =>
        page.FindAll("tbody tr.fluent-data-grid-row").Single(r => r.TextContent.Contains(name));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
