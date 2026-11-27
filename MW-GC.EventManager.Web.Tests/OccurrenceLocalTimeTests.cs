using System.Globalization;
using Bunit;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// #204 story 19, #268 item 1: the Events list and the Event details dialog show an Occurrence's time in the browser's
// time zone. The zone is pinned to Kathmandu (UTC+05:45, no daylight saving), never UTC, and the Occurrence is stored
// at its Series' UTC-04:00, so dropping ToLocalTime() shows the stored offset's time and fails on any machine. 19:00 on
// 15 October at UTC-04:00 is 23:00 UTC, which is 04:45 on Friday 16 October in Kathmandu: the date and the time move.
[TestClass]
public sealed class OccurrenceLocalTimeTests : IDisposable
{
    private static readonly TimeSpan Kathmandu = new(5, 45, 0);

    private readonly string? zone = Environment.GetEnvironmentVariable("TZ");
    private readonly CultureInfo culture = CultureInfo.CurrentCulture;
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;

    public OccurrenceLocalTimeTests()
    {
        Environment.SetEnvironmentVariable("TZ", "Asia/Kathmandu");
        TimeZoneInfo.ClearCachedData();
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var start = new DateTimeOffset(2026, 10, 15, 19, 0, 0, TimeSpan.FromHours(-4));
        var raid = new EventEntity { Name = "Weekly raid", Date = start, SeriesId = Guid.NewGuid(), OccurrenceKey = start };
        api.Json(HttpMethod.Get, "/api/events", new[] { raid })
           .Json(HttpMethod.Get, "/api/games", Array.Empty<GameEntity>())
           .Json(HttpMethod.Get, "/api/activities", Array.Empty<ActivityEntity>())
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>());
        harness = new PageHarness(api);
    }

    public void Dispose()
    {
        harness.Dispose();
        Environment.SetEnvironmentVariable("TZ", zone);
        TimeZoneInfo.ClearCachedData();
        CultureInfo.CurrentCulture = culture;
    }

    [TestMethod]
    public void TheList_ShowsTheOccurrencesTime_InTheBrowsersZone()
    {
        var page = Loaded();

        var row = page.FindAll("tbody tr.fluent-data-grid-row").Single(r => r.TextContent.Contains("Weekly raid"));
        Assert.AreEqual("10/16/2026 04:45", Normalize(row.QuerySelectorAll("td")[1].TextContent));
    }

    [TestMethod]
    public async Task TheDetailsDialog_ShowsTheOccurrencesTime_InTheBrowsersZone()
    {
        var page = Loaded();

        await RowButton(page, "Weekly raid", "Details").ClickAsync(new());

        var labels = page.FormDialog().QuerySelectorAll(".fluent-typography").Select(l => Normalize(l.TextContent)).ToList();
        Assert.AreEqual("Weekly raid", labels[0]);
        Assert.AreEqual("Friday, 16 October 2026 04:45", labels[1]);
    }

    private IRenderedComponent<Events> Loaded()
    {
        if (TimeZoneInfo.Local.BaseUtcOffset != Kathmandu) Assert.Inconclusive("This platform does not take the local zone from TZ.");
        var page = harness.Render<Events>();
        page.WaitForRows(1);
        return page;
    }
}
