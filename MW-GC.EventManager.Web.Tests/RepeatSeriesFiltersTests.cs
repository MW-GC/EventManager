using System.Net;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;

namespace MW_GC.EventManager.Web.Tests;

// #204, #232: a create with Repeat on sends the dialog's avoid recent, players tonight and longest duration in the
// Series' Filters, each as the Filter's own setting (N, players, minutes) and null while its box is off: empty, or
// text the Filters panel does not take. The three values used are distinct, so a swapped or dropped one shows.
[TestClass]
public sealed class RepeatSeriesFiltersTests : IDisposable
{
    private const string AvoidRecent = "Recent Events to avoid";
    private const string Players = "Players tonight";
    private const string Duration = "Longest duration (minutes)";
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;

    public RepeatSeriesFiltersTests()
    {
        GameEntity kart = new() { Name = "Kart" }, brawl = new() { Name = "Brawl" }, puzzle = new() { Name = "Puzzle" };
        api.Json(HttpMethod.Get, "/api/games", new[] { kart, brawl, puzzle })
           .Json(HttpMethod.Get, "/api/activities", new[]
           {
               new ActivityEntity { Name = "Grand Prix", GameId = kart.Id },
               new ActivityEntity { Name = "Stock", GameId = brawl.Id },
               new ActivityEntity { Name = "Tiles", GameId = puzzle.Id },
           })
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>())
           .Json(HttpMethod.Get, "/api/events", Array.Empty<EventEntity>())
           .Status(HttpMethod.Post, "/api/series", HttpStatusCode.Created, "{}");
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    [TestMethod]
    public async Task AllThreeBoxesFilled_SendsEachInTheSeriesFilters()
    {
        var page = await OpenRepeatingCreate();
        await Type(page, AvoidRecent, "4");
        await Type(page, Players, "6");
        await Type(page, Duration, "90");

        var (series, _) = await CreateSeries(page);

        Assert.AreEqual((4, 6, 90), (series.Filters.AvoidRecent, series.Filters.PlayersTonight, series.Filters.LongestDuration));
    }

    // Every box empty: all three null and left out of the body, exactly as a client that did not know them sent it.
    [TestMethod]
    public async Task EveryBoxEmpty_SendsAllThreeOff_AsAnOldClientDid()
    {
        var page = await OpenRepeatingCreate();

        var (series, body) = await CreateSeries(page);

        Assert.AreEqual(((int?)null, (int?)null, (int?)null), (series.Filters.AvoidRecent, series.Filters.PlayersTonight, series.Filters.LongestDuration));
        foreach (var name in new[] { "avoidRecent", "playersTonight", "longestDuration" })
            Assert.DoesNotContain($"\"{name}\"", body);
    }

    [TestMethod]
    [DataRow(AvoidRecent, "7", 7, null, null)]
    [DataRow(Players, "7", null, 7, null)]
    [DataRow(Duration, "7", null, null, 7)]
    public async Task OnlyOneBoxFilled_SendsThatOne_AndTheOthersOff(string box, string text, int? avoid, int? players, int? duration)
    {
        var page = await OpenRepeatingCreate();
        await Type(page, box, text);

        var (series, _) = await CreateSeries(page);

        Assert.AreEqual((avoid, players, duration), (series.Filters.AvoidRecent, series.Filters.PlayersTonight, series.Filters.LongestDuration));
    }

    // One box holds text the Filters panel refuses (its message shows and it filters nothing): that one is sent as off,
    // the other two, valid, still go. Out-of-range and malformed rows for each box.
    [TestMethod]
    [DataRow(AvoidRecent, "abc")]
    [DataRow(AvoidRecent, "0")]
    [DataRow(AvoidRecent, "11")]
    [DataRow(AvoidRecent, "2.0")]
    [DataRow(Players, "abc")]
    [DataRow(Players, "0")]
    [DataRow(Players, "100")]
    [DataRow(Players, "1e1")]
    [DataRow(Duration, "abc")]
    [DataRow(Duration, "-5")]
    [DataRow(Duration, "1441")]
    [DataRow(Duration, "1,0")]
    public async Task ABoxWithInvalidText_SendsThatOneOff_AndTheValidOnes(string box, string text)
    {
        var page = await OpenRepeatingCreate();
        await Type(page, AvoidRecent, "4");
        await Type(page, Players, "6");
        await Type(page, Duration, "90");
        await Type(page, box, text);
        Assert.HasCount(1, page.FormDialog().Alerts(), "the box shows its message");

        var (series, _) = await CreateSeries(page);

        Assert.AreEqual((box == AvoidRecent ? null : 4, box == Players ? null : 6, box == Duration ? null : (int?)90),
            (series.Filters.AvoidRecent, series.Filters.PlayersTonight, series.Filters.LongestDuration));
    }

    // The avoid-recent box takes exactly the API's limit (Shared's SeriesFilters.MaxAvoidRecent) and refuses one more,
    // so a Series the dialog sends is never refused for it and none it could send is cut short.
    [TestMethod]
    public async Task TheAvoidRecentLimit_IsTheApisSeriesFiltersMaxAvoidRecent()
    {
        var page = await OpenRepeatingCreate();
        await Type(page, AvoidRecent, (SeriesFilters.MaxAvoidRecent + 1).ToString());
        Assert.AreEqual($"Avoid recent must be a whole number from 1 to {SeriesFilters.MaxAvoidRecent}.", Assert.ContainsSingle(page.FormDialog().Alerts()));
        await Type(page, AvoidRecent, SeriesFilters.MaxAvoidRecent.ToString());
        Assert.IsEmpty(page.FormDialog().Alerts());

        var (series, _) = await CreateSeries(page);

        Assert.AreEqual(SeriesFilters.MaxAvoidRecent, series.Filters.AvoidRecent);
    }

    // ---- helpers -------------------------------------------------------------------------------

    // A create dialog named Every Thursday, for 10 December 2026, with Repeat switched on (weekly).
    private async Task<IRenderedComponent<Events>> OpenRepeatingCreate()
    {
        var page = harness.Render<Events>();
        page.WaitForAssertion(() => Assert.AreEqual(1, api.Count(HttpMethod.Get, "/api/events")));
        await page.Button("Create Event").ClickAsync(new());
        await page.FormDialog().QuerySelector("fluent-text-field[placeholder='Enter event name...']")!.ChangeAsync(new ChangeEventArgs { Value = "Every Thursday" });
        await page.FormDialog().QuerySelector("fluent-text-field.fluent-datepicker")!.ChangeAsync(new ChangeEventArgs { Value = "12/10/2026" });
        await page.FormDialog().QuerySelectorAll("fluent-switch").Single(s => s.GetAttribute("aria-label") == "Repeat")
            .TriggerEventAsync("onswitchcheckedchange", new CheckboxChangeEventArgs { Checked = true });
        return page;
    }

    private static Task Type(IRenderedComponent<Events> page, string box, string text) =>
        (page.FormDialog().QuerySelector($"fluent-text-field[aria-label='{box}']") ?? throw new AssertFailedException($"No box named {box}."))
            .InputAsync(new ChangeEventArgs { Value = text });

    private async Task<(SeriesEntity Series, string Body)> CreateSeries(IRenderedComponent<Events> page)
    {
        await page.FormDialog().Button("Create Event").ClickAsync(new());
        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog(), string.Join(" | ", page.HasFormDialog() ? page.FormDialog().Alerts() : [])));
        var body = Assert.ContainsSingle(api.Bodies(HttpMethod.Post, "/api/series"))!;
        return (JsonSerializer.Deserialize<SeriesEntity>(body, JsonSerializerOptions.Web)!, body);
    }
}
