using System.Globalization;
using System.Net;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Series;
using MW_GC.EventManager.Web.Pages;
using MW_GC.EventManager.Web.Shared.Events;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// #100, #204: the Repeat section of the create Event dialog on the real Events page, over a stub API. The browser's
// zone is pinned to Kathmandu (UTC+05:45, never UTC, no daylight saving) so a start sent in UTC instead of the local
// offset fails even on a UTC machine. The Event is typed for 10 December 2026, a Thursday in the second week, at the
// dialog's 19:00, so the start the dialog sends is written out as "2026-12-10T19:00:00+05:45".
[TestClass]
public sealed class RepeatSectionDialogTests : IDisposable
{
    private const string Start = "2026-12-10T19:00:00+05:45";
    private const string EndMessage = "End must be after the start.";
    private static readonly TimeSpan Kathmandu = new(5, 45, 0);
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly GameEntity kart = new() { Name = "Kart" }, brawl = new() { Name = "Brawl" }, puzzle = new() { Name = "Puzzle" };
    private readonly ThemeEntity spooky = new() { Name = "Spooky" };
    private readonly HolidayEntity halloween = new() { Name = "Halloween" };
    private readonly EventEntity night;
    private readonly string? zone = Environment.GetEnvironmentVariable("TZ");
    private readonly CultureInfo culture = CultureInfo.CurrentCulture;

    public RepeatSectionDialogTests()
    {
        Environment.SetEnvironmentVariable("TZ", "Asia/Kathmandu");
        TimeZoneInfo.ClearCachedData();
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var grandPrix = new ActivityEntity { Name = "Grand Prix", GameId = kart.Id };
        var stock = new ActivityEntity { Name = "Stock", GameId = brawl.Id };
        var tiles = new ActivityEntity { Name = "Tiles", GameId = puzzle.Id };
        night = new EventEntity { Name = "Game night", Date = new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero), Selections = [Pick(kart, grandPrix)] };
        api.Json(HttpMethod.Get, "/api/games", new[] { kart, brawl, puzzle })
           .Json(HttpMethod.Get, "/api/activities", new[] { grandPrix, stock, tiles })
           .Json(HttpMethod.Get, "/api/themes", new[] { spooky })
           .Json(HttpMethod.Get, "/api/holidays", new[] { halloween })
           .Json(HttpMethod.Get, "/api/events", new[] { night })
           .Status(HttpMethod.Post, "/api/series", HttpStatusCode.Created, "{}")
           .Status(HttpMethod.Post, "/api/events", HttpStatusCode.Created, "{}");
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
    public async Task Repeat_IsOffWhenTheDialogOpens_AndOffAgainAfterReopening()
    {
        var page = await OpenCreate();
        AssertOff(page);

        await SwitchRepeat(page, true);
        Assert.IsTrue(IsOn(page));
        Assert.AreEqual("Weekly", Select(page, "Repeats").QuerySelectorAll("fluent-option").First().TextContent.Trim());
        Assert.AreEqual(nameof(RecurrenceKind.Weekly), Select(page, "Repeats").GetAttribute("current-value"));
        // The hint under the switch says the roll shown is not saved.
        Assert.AreEqual("Each repeat starts with no picks yet: the activities rolled below are not saved.",
            Section(page).QuerySelector(".repeat-hint")!.TextContent.Trim());
        await page.FormDialog().Button("Cancel").ClickAsync(new());

        await page.Button("Create Event").ClickAsync(new());
        AssertOff(page);
    }

    [TestMethod]
    [DataRow(RecurrenceKind.Weekly, new string[0])]
    [DataRow(RecurrenceKind.BiWeekly, new string[0])]
    [DataRow(RecurrenceKind.MonthlyNthWeekday, new[] { "Week of the month", "Weekday" })]
    [DataRow(RecurrenceKind.MonthlySameDate, new string[0])]
    [DataRow(RecurrenceKind.AnnualSameDate, new string[0])]
    [DataRow(RecurrenceKind.AnnualHoliday, new[] { "Holiday" })]
    public async Task EachRule_ShowsExactlyItsExtraSelects(RecurrenceKind kind, string[] extra)
    {
        var page = await OpenCreate();
        await SwitchRepeat(page, true);

        await Choose(page, "Repeats", kind.ToString());

        CollectionAssert.AreEqual(new[] { "Repeats" }.Concat(extra).ToArray(),
            Section(page).QuerySelectorAll("fluent-select").Select(s => s.GetAttribute("aria-label")).ToArray());
    }

    [TestMethod]
    [DataRow(RecurrenceKind.Weekly, null, null, null)]
    [DataRow(RecurrenceKind.BiWeekly, null, null, null)]
    [DataRow(RecurrenceKind.MonthlyNthWeekday, WeekOfMonth.Second, DayOfWeek.Thursday, null)]
    [DataRow(RecurrenceKind.MonthlySameDate, null, null, null)]
    [DataRow(RecurrenceKind.AnnualSameDate, null, null, null)]
    [DataRow(RecurrenceKind.AnnualHoliday, null, null, HolidayPreset.NewYearsDay)]
    public async Task CreateWithRepeatOn_SendsOneSeries_WithTheRuleAndTheStartsOffset_AndNoEvent(
        RecurrenceKind kind, WeekOfMonth? week, DayOfWeek? weekday, HolidayPreset? preset)
    {
        var page = await OpenCreate();
        await SwitchRepeat(page, true);
        await Choose(page, "Repeats", kind.ToString());

        var (series, body) = await CreateSeries(page);

        Assert.AreEqual(new RecurrenceRule(kind, week, weekday, preset), series.Schedule.Rule);
        AssertStart(series, body);
        Assert.IsNull(series.Schedule.End);
        Assert.AreEqual("Every Thursday", series.Name);
    }

    // The extra selects send what the admin picks, not the date's defaults (Second Thursday, no holiday on the date).
    [TestMethod]
    public async Task TheChosenWeekWeekdayAndHoliday_AreSent()
    {
        var page = await OpenCreate();
        await SwitchRepeat(page, true);
        await Choose(page, "Repeats", nameof(RecurrenceKind.MonthlyNthWeekday));
        Assert.AreEqual("Second", Select(page, "Week of the month").GetAttribute("current-value"));
        Assert.AreEqual("Thursday", Select(page, "Weekday").GetAttribute("current-value"));
        await Choose(page, "Week of the month", "Last");
        await Choose(page, "Weekday", "Friday");

        var (series, _) = await CreateSeries(page);
        Assert.AreEqual(RecurrenceRule.MonthlyNthWeekday(WeekOfMonth.Last, DayOfWeek.Friday), series.Schedule.Rule);

        await OpenCreate(page);
        await SwitchRepeat(page, true);
        await Choose(page, "Repeats", nameof(RecurrenceKind.AnnualHoliday));
        Assert.AreEqual("Thanksgiving", Select(page, "Holiday").QuerySelectorAll("fluent-option")
            .Single(o => o.GetAttribute("value") == nameof(HolidayPreset.Thanksgiving)).TextContent.Trim());
        await Choose(page, "Holiday", nameof(HolidayPreset.Thanksgiving));
        (series, _) = await CreateSeries(page);
        Assert.AreEqual(RecurrenceRule.AnnualHoliday(HolidayPreset.Thanksgiving), series.Schedule.Rule);
    }

    // The slot count is the dialog's (two after removing one of three), Unique games only and every Filter are sent.
    [TestMethod]
    public async Task TheSeries_CarriesTheDialogsSlotCount_UniqueGamesOnly_AndFilters()
    {
        var page = await OpenCreate();
        await SwitchRepeat(page, true);
        await page.FormDialog().QuerySelector("fluent-button[aria-label='Remove slot 1']")!.ClickAsync(new());
        await Check(page, "Filter by games", "Brawl");
        await Check(page, "Filter by themes", "Spooky");
        await Check(page, "Filter by holidays", "Halloween");
        await SwitchNamed(page, "Themed Activities Only").TriggerEventAsync("onswitchcheckedchange", new CheckboxChangeEventArgs { Checked = true });
        await SwitchNamed(page, "Unique games only").TriggerEventAsync("onswitchcheckedchange", new CheckboxChangeEventArgs { Checked = false });

        var (series, _) = await CreateSeries(page);

        Assert.AreEqual(2, series.SlotCount);
        Assert.IsFalse(series.UniqueGamesOnly);
        Assert.IsTrue(series.Filters.ThemedOnly);
        CollectionAssert.AreEqual(new[] { brawl.Id }, series.Filters.GameIds);
        CollectionAssert.AreEqual(new[] { spooky.Id }, series.Filters.ThemeIds);
        CollectionAssert.AreEqual(new[] { halloween.Id }, series.Filters.HolidayIds);
    }

    [TestMethod]
    public async Task AnEndDate_IsSentAtTheStartsTimeAndOffset_AndOneNotAfterTheStartDisablesCreate()
    {
        var page = await OpenCreate();
        await SwitchRepeat(page, true);

        await TypeEnd(page, "12/10/2026");
        Assert.AreEqual(EndMessage, Assert.ContainsSingle(Section(page).Alerts()));
        Assert.IsTrue(page.FormDialog().Button("Create Event").HasAttribute("disabled"));

        await TypeEnd(page, "03/04/2027");
        Assert.IsEmpty(Section(page).Alerts());
        Assert.IsFalse(page.FormDialog().Button("Create Event").HasAttribute("disabled"));
        var (series, body) = await CreateSeries(page);

        Assert.AreEqual(new DateTimeOffset(2027, 3, 4, 19, 0, 0, Kathmandu), series.Schedule.End);
        StringAssert.Contains(body, "\"end\":\"2027-03-04T19:00:00+05:45\"");
    }

    [TestMethod]
    public async Task CreateWithRepeatOff_SendsTheEventAsBefore_AndNoSeries()
    {
        var page = await OpenCreate();

        await page.FormDialog().Button("Create Event").ClickAsync(new());

        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        Assert.AreEqual(0, api.Count(HttpMethod.Post, "/api/series"));
        var sent = JsonSerializer.Deserialize<EventEntity>(Assert.ContainsSingle(api.Bodies(HttpMethod.Post, "/api/events"))!, JsonSerializerOptions.Web)!;
        Assert.AreEqual("Every Thursday", sent.Name);
        Assert.HasCount(3, sent.Selections);
        Assert.IsNull(sent.SeriesId);
    }

    // A create whose answer was lost keeps its key, so the retry sends the same one; a new opening gets a new one.
    [TestMethod]
    public async Task ARetryAfterALostAnswer_SendsTheSameIdempotencyKey()
    {
        api.Offline(HttpMethod.Post, "/api/series");
        var page = await OpenCreate();
        await SwitchRepeat(page, true);
        await page.FormDialog().Button("Create Event").ClickAsync(new());
        page.WaitForAssertion(() => Assert.IsNotEmpty(page.FormDialog().Alerts()));

        api.Status(HttpMethod.Post, "/api/series", HttpStatusCode.Created, "{}");
        await page.FormDialog().Button("Create Event").ClickAsync(new());
        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));

        var keys = api.IdempotencyKeys(HttpMethod.Post, "/api/series");
        Assert.HasCount(2, keys);
        Assert.IsTrue(Guid.TryParseExact(keys[0], "D", out var key) && key != Guid.Empty, keys[0]);
        Assert.AreEqual(keys[0], keys[1]);
        await OpenCreate(page);
        await SwitchRepeat(page, true);
        await page.FormDialog().Button("Create Event").ClickAsync(new());
        page.WaitForAssertion(() => Assert.HasCount(3, api.IdempotencyKeys(HttpMethod.Post, "/api/series")));
        Assert.AreNotEqual(keys[0], api.IdempotencyKeys(HttpMethod.Post, "/api/series")[2]);
    }

    [TestMethod]
    public async Task AnApiRefusal_ShowsItsMessageInTheAlert_AndKeepsTheDialogOpen()
    {
        api.Status(HttpMethod.Post, "/api/series", HttpStatusCode.BadRequest, "\"End must be after the start.\"");
        var page = await OpenCreate();
        await SwitchRepeat(page, true);

        await page.FormDialog().Button("Create Event").ClickAsync(new());

        page.WaitForAssertion(() => Assert.AreEqual("The event was not saved. End must be after the start.", Assert.ContainsSingle(page.FormDialog().Alerts())));
        Assert.IsTrue(page.HasFormDialog());
        Assert.AreEqual(1, api.Count(HttpMethod.Post, "/api/series"));
        Assert.AreEqual(0, api.Count(HttpMethod.Post, "/api/events"));
    }

    // A saved Series closes the dialog and reloads the list, so its Occurrences show.
    [TestMethod]
    public async Task ASavedSeries_ClosesTheDialog_AndReloadsTheList()
    {
        var page = await OpenCreate();
        await SwitchRepeat(page, true);
        var reads = api.Count(HttpMethod.Get, "/api/events");

        await page.FormDialog().Button("Create Event").ClickAsync(new());

        page.WaitForAssertion(() => Assert.AreEqual(reads + 1, api.Count(HttpMethod.Get, "/api/events")));
        Assert.IsFalse(page.HasFormDialog());
        Assert.IsEmpty(page.PageAlerts());
    }

    [TestMethod]
    public async Task TheEditDialog_NeverShowsTheRepeatSection()
    {
        var page = await OpenPage();
        await page.Button("Create Event").ClickAsync(new());
        await SwitchRepeat(page, true);
        await page.FormDialog().Button("Cancel").ClickAsync(new());

        await RowButton(page, "Game night", "Edit").ClickAsync(new());

        Assert.AreEqual("Edit Event", page.DialogTitle());
        Assert.IsNull(page.FormDialog().QuerySelector(".repeat-section"));
        Assert.IsFalse(page.FormDialog().QuerySelectorAll("fluent-switch").Any(s => s.GetAttribute("aria-label") == "Repeat"));
    }

    // Every new control has its accessible name, and a switch and a select a keyboard reaches (no tabindex=-1).
    [TestMethod]
    public async Task EveryRepeatControl_HasItsAccessibleName_AndIsInTheTabOrder()
    {
        var page = await OpenCreate();
        await SwitchRepeat(page, true);
        await Choose(page, "Repeats", nameof(RecurrenceKind.MonthlyNthWeekday));

        var controls = new List<IElement> { RepeatSwitch(page) };
        controls.AddRange(Section(page).QuerySelectorAll("fluent-select"));
        controls.Add(EndBox(page));
        CollectionAssert.AreEqual(new[] { "Repeat", "Repeats", "Week of the month", "Weekday", "Ends on (optional)" },
            controls.Select(c => c.GetAttribute("aria-label")).ToArray());
        Assert.IsFalse(controls.Any(c => c.GetAttribute("tabindex") == "-1" || c.HasAttribute("disabled")));
    }

    // ---- helpers -------------------------------------------------------------------------------

    private async Task<IRenderedComponent<Events>> OpenPage()
    {
        if (TimeZoneInfo.Local.BaseUtcOffset != Kathmandu) Assert.Inconclusive("This platform does not take the local zone from TZ.");
        var page = harness.Render<Events>();
        page.WaitForRows(1);
        return await Task.FromResult(page);
    }

    private async Task<IRenderedComponent<Events>> OpenCreate()
    {
        var page = await OpenPage();
        await OpenCreate(page);
        return page;
    }

    // A create dialog named Every Thursday, for 10 December 2026 at the dialog's 19:00.
    private static async Task OpenCreate(IRenderedComponent<Events> page)
    {
        await page.Button("Create Event").ClickAsync(new());
        await page.FormDialog().QuerySelector("fluent-text-field[placeholder='Enter event name...']")!.ChangeAsync(new ChangeEventArgs { Value = "Every Thursday" });
        await page.FormDialog().QuerySelector("fluent-text-field.fluent-datepicker")!.ChangeAsync(new ChangeEventArgs { Value = "12/10/2026" });
    }

    private async Task<(SeriesEntity Series, string Body)> CreateSeries(IRenderedComponent<Events> page)
    {
        var before = api.Count(HttpMethod.Post, "/api/series");
        await page.FormDialog().Button("Create Event").ClickAsync(new());
        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog(), string.Join(" | ", page.HasFormDialog() ? page.FormDialog().Alerts() : [])));
        var bodies = api.Bodies(HttpMethod.Post, "/api/series");
        Assert.AreEqual(before + 1, bodies.Count, "one Series per create");
        Assert.AreEqual(0, api.Count(HttpMethod.Post, "/api/events"), "a repeating create sends no Event");
        return (JsonSerializer.Deserialize<SeriesEntity>(bodies[^1]!, JsonSerializerOptions.Web)!, bodies[^1]!);
    }

    private static void AssertStart(SeriesEntity series, string body)
    {
        Assert.AreEqual(new DateTimeOffset(2026, 12, 10, 19, 0, 0, Kathmandu), series.Schedule.First);
        Assert.AreEqual(Kathmandu, series.Schedule.First.Offset);
        StringAssert.Contains(body, $"\"first\":\"{Start}\"");
    }

    private static void AssertOff(IRenderedComponent<Events> page)
    {
        Assert.IsFalse(IsOn(page));
        Assert.IsEmpty(Section(page).QuerySelectorAll("fluent-select"));
        Assert.IsNull(Section(page).QuerySelector(".repeat-hint"));
    }

    private static IElement Section(IRenderedComponent<Events> page) => page.FormDialog().QuerySelector(".repeat-section")!;

    private static IElement SwitchNamed(IRenderedComponent<Events> page, string label) =>
        page.FormDialog().QuerySelectorAll("fluent-switch").Single(s => s.GetAttribute("aria-label") == label);

    private static IElement RepeatSwitch(IRenderedComponent<Events> page) => SwitchNamed(page, "Repeat");

    private static bool IsOn(IRenderedComponent<Events> page) => RepeatSwitch(page).HasAttribute("current-checked");

    private static Task SwitchRepeat(IRenderedComponent<Events> page, bool on) =>
        RepeatSwitch(page).TriggerEventAsync("onswitchcheckedchange", new CheckboxChangeEventArgs { Checked = on });

    private static IElement Select(IRenderedComponent<Events> page, string label) =>
        Section(page).QuerySelectorAll("fluent-select").Single(s => s.GetAttribute("aria-label") == label);

    private static Task Choose(IRenderedComponent<Events> page, string label, string value) =>
        Select(page, label).QuerySelectorAll("fluent-option").Single(o => o.GetAttribute("value") == value).ClickAsync(new());

    private static Task Check(IRenderedComponent<Events> page, string group, string label) =>
        page.FormDialog().QuerySelector($"[role=group][aria-label='{group}']")!.QuerySelectorAll("fluent-checkbox")
            .Single(c => c.TextContent.Trim() == label).TriggerEventAsync("oncheckedchange", new CheckboxChangeEventArgs { Checked = true });

    private static IElement EndBox(IRenderedComponent<Events> page) => Section(page).QuerySelector("fluent-text-field.fluent-datepicker")!;

    private static Task TypeEnd(IRenderedComponent<Events> page, string date) => EndBox(page).ChangeAsync(new ChangeEventArgs { Value = date });
}
