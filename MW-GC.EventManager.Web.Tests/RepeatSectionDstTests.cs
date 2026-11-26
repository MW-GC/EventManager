using System.Globalization;
using System.Net;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Series;
using MW_GC.EventManager.Web.Pages;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// #233.3: the Repeat section's optional end date across both daylight saving changes. The browser's zone is pinned to
// Chicago (UTC-06:00 in winter, UTC-05:00 in summer; 2026 ends daylight saving on 1 November, 2027 starts it on 14
// March), so a start and its end date fall on either side of a change and the viewer's offset on the end date is not
// the Series' own. The end must be the end date at the start's time of day and UTC offset (ADR 0002), so the weekly
// Occurrence on the end date, which keeps the start's UTC time, is made. Every instant is written out by hand.
[TestClass]
public sealed class RepeatSectionDstTests : IDisposable
{
    private static readonly TimeSpan Winter = TimeSpan.FromHours(-6), Summer = TimeSpan.FromHours(-5);
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly string? zone = Environment.GetEnvironmentVariable("TZ");
    private readonly CultureInfo culture = CultureInfo.CurrentCulture;

    public RepeatSectionDstTests()
    {
        Environment.SetEnvironmentVariable("TZ", "America/Chicago");
        TimeZoneInfo.ClearCachedData();
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var kart = new GameEntity { Name = "Kart" };
        var grandPrix = new ActivityEntity { Name = "Grand Prix", GameId = kart.Id };
        var night = new EventEntity { Name = "Game night", Date = new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero), Selections = [Pick(kart, grandPrix)] };
        api.Json(HttpMethod.Get, "/api/games", new[] { kart })
           .Json(HttpMethod.Get, "/api/activities", new[] { grandPrix })
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>())
           .Json(HttpMethod.Get, "/api/events", new[] { night })
           .Status(HttpMethod.Post, "/api/series", HttpStatusCode.Created, "{}");
        harness = new PageHarness(api);
    }

    public void Dispose()
    {
        harness.Dispose();
        Environment.SetEnvironmentVariable("TZ", zone);
        TimeZoneInfo.ClearCachedData();
        CultureInfo.CurrentCulture = culture;
    }

    // Spring: the end date is in summer time, an hour ahead of the start's winter offset. Read at the viewer's offset,
    // the end would be 00:00 UTC on 19 March, an hour before the Occurrence on the 18th (01:00 UTC), and lose it.
    [TestMethod]
    public async Task AnEndDateAfterTheSpringChange_IsAtTheStartsWinterOffset_AndKeepsTheOccurrenceOnIt()
    {
        var (series, body) = await CreateWeekly("03/04/2027", "03/18/2027");

        Assert.AreEqual(new DateTimeOffset(2027, 3, 4, 19, 0, 0, Winter), series.Schedule.First);
        StringAssert.Contains(body, "\"end\":\"2027-03-18T19:00:00-06:00\"");
        CollectionAssert.AreEqual(
            new[] { "2027-03-04T19:00:00-06:00", "2027-03-11T19:00:00-06:00", "2027-03-18T19:00:00-06:00" },
            Occurrences(series, now: new DateTimeOffset(2027, 3, 1, 12, 0, 0, Winter)));
    }

    // Autumn: the end date is in winter time, an hour behind the start's summer offset. Read at the viewer's offset,
    // the end would be 01:00 UTC on 6 November instead of 00:00.
    [TestMethod]
    public async Task AnEndDateAfterTheAutumnChange_IsAtTheStartsSummerOffset_AndKeepsTheOccurrenceOnIt()
    {
        var (series, body) = await CreateWeekly("10/22/2026", "11/05/2026");

        Assert.AreEqual(new DateTimeOffset(2026, 10, 22, 19, 0, 0, Summer), series.Schedule.First);
        StringAssert.Contains(body, "\"end\":\"2026-11-05T19:00:00-05:00\"");
        Assert.AreEqual(new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero), series.Schedule.End);
        CollectionAssert.AreEqual(
            new[] { "2026-10-22T19:00:00-05:00", "2026-10-29T19:00:00-05:00", "2026-11-05T19:00:00-05:00" },
            Occurrences(series, now: new DateTimeOffset(2026, 10, 20, 12, 0, 0, Summer)));
    }

    // ---- helpers -------------------------------------------------------------------------------

    // A weekly create dialog for the start date at the dialog's 19:00 with this end date, saved; the Series it sends.
    private async Task<(SeriesEntity Series, string Body)> CreateWeekly(string start, string end)
    {
        if (TimeZoneInfo.Local.BaseUtcOffset != Winter || !TimeZoneInfo.Local.SupportsDaylightSavingTime)
            Assert.Inconclusive("This platform does not take the local zone from TZ.");
        var page = harness.Render<Events>();
        page.WaitForRows(1);
        await page.Button("Create Event").ClickAsync(new());
        var dialog = page.FormDialog();
        await dialog.QuerySelector("fluent-text-field[placeholder='Enter event name...']")!.ChangeAsync(new ChangeEventArgs { Value = "Every Thursday" });
        await dialog.QuerySelector("fluent-text-field.fluent-datepicker")!.ChangeAsync(new ChangeEventArgs { Value = start });
        await page.FormDialog().QuerySelectorAll("fluent-switch").Single(s => s.GetAttribute("aria-label") == "Repeat")
            .TriggerEventAsync("onswitchcheckedchange", new CheckboxChangeEventArgs { Checked = true });
        await page.FormDialog().QuerySelector(".repeat-section fluent-text-field.fluent-datepicker")!.ChangeAsync(new ChangeEventArgs { Value = end });

        await page.FormDialog().Button("Create Event").ClickAsync(new());

        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog(), string.Join(" | ", page.HasFormDialog() ? page.FormDialog().Alerts() : [])));
        var body = Assert.ContainsSingle(api.Bodies(HttpMethod.Post, "/api/series"))!;
        return (JsonSerializer.Deserialize<SeriesEntity>(body, JsonSerializerOptions.Web)!, body);
    }

    // The Occurrences the API's generator makes from the sent schedule, written as the Series' own wall clock.
    private static string[] Occurrences(SeriesEntity series, DateTimeOffset now) =>
        OccurrenceGenerator.Generate(series.Schedule, now, [], [])
            .Select(i => i.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)).ToArray();
}
