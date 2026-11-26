using System.Net;
using Bunit;
using Microsoft.FluentUI.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// #246: how the edit dialog of an Occurrence not rolled yet takes its Series' Filters. Item 1: a Game, Theme or Holiday
// deleted since the Series was saved is not seeded (the panel could not draw it, so it could not be cleared). Item 4:
// Unique games only stays the Occurrence's own. Item 5: a Series read that finishes after the dialog has drawn itself
// still shows. The library is built so each Filter is the only thing that keeps one Activity out of the roll: Alpha's
// Cozy (Theme), Beta's Yule (Holiday) and Gamma's Gamma run (Game). Fixture: OccurrenceEditFixture's Raid Series, here
// with slot count 2 and players tonight 4 (no Activity has a player limit, so the box only shows the read is in).
[TestClass]
public sealed class OccurrenceEditSeriesSeedTests : OccurrenceEditFixture
{
    private const string PlayersBox = "Players tonight";

    private readonly ThemeEntity spooky = new() { Name = "Spooky" }, cozy = new() { Name = "Cozy" };
    private readonly HolidayEntity halloween = new() { Name = "Halloween" }, yule = new() { Name = "Yule" };
    private readonly GameEntity alpha = new() { Name = "Alpha" }, beta = new() { Name = "Beta" }, gamma = new() { Name = "Gamma" };
    // Deleted since the Series was saved: in no library list.
    private readonly Guid goneGame = Guid.NewGuid(), goneTheme = Guid.NewGuid(), goneHoliday = Guid.NewGuid();
    private readonly ActivityEntity alphaRun, betaRun;

    public OccurrenceEditSeriesSeedTests()
    {
        alphaRun = Tagged(alpha, "Alpha run", spooky, halloween);
        betaRun = Tagged(beta, "Beta run", spooky, halloween);
        api.Json(HttpMethod.Get, "/api/games", new[] { alpha, beta, gamma })
           .Json(HttpMethod.Get, "/api/activities", new[]
           {
               alphaRun, betaRun, Tagged(alpha, "Cozy", cozy, halloween), Tagged(beta, "Yule", spooky, yule), Tagged(gamma, "Gamma run", spooky, halloween),
           })
           .Json(HttpMethod.Get, "/api/themes", new[] { spooky, cozy })
           .Json(HttpMethod.Get, "/api/holidays", new[] { halloween, yule });
        raidSeries.SlotCount = 2;
        raidSeries.Filters = new SeriesFilters
        {
            GameIds = [goneGame, alpha.Id, beta.Id], ThemeIds = [spooky.Id, goneTheme], HolidayIds = [goneHoliday, halloween.Id], PlayersTonight = 4,
        };
    }

    [TestMethod]
    public async Task AStaleSeriesFilterId_IsNotSeeded_TheLibrarysAreTicked_AndRandomizeAllRollsWithThem()
    {
        var page = await Seeded();

        CollectionAssert.AreEquivalent(new[] { "Alpha", "Beta" }, Checked(page, "Filter by games"));
        CollectionAssert.AreEqual(new[] { "Spooky" }, Checked(page, "Filter by themes"));
        CollectionAssert.AreEqual(new[] { "Halloween" }, Checked(page, "Filter by holidays"));
        Assert.IsEmpty(page.FormDialog().Alerts());
        for (var i = 0; i < 5; i++)
        {
            await Roll(page);
            CollectionAssert.AreEquivalent(new[] { alphaRun.Id.ToString(), betaRun.Id.ToString() }, SlotActivities(page), $"roll {i}");
        }
        Assert.IsEmpty(page.FormDialog().Alerts());
    }

    [TestMethod]
    public async Task ASeededOccurrenceWithStaleIds_ClosedWithoutATouch_DoesNotAskToDiscard()
    {
        var page = await Seeded();

        await Dismiss(page);

        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        Assert.IsFalse(page.HasConfirmation());
    }

    // The ids the panel shows are all the Filter holds: unticking them turns it off, so the roll is not left with only
    // a deleted id to match (and nothing to roll).
    [TestMethod]
    [DataRow("Filter by games", "Alpha,Beta")]
    [DataRow("Filter by themes", "Spooky")]
    [DataRow("Filter by holidays", "Halloween")]
    public async Task UntickingEveryShownIdOfAGroup_TurnsThatFilterOff(string group, string shown)
    {
        var page = await Seeded();

        foreach (var label in shown.Split(',')) await Tick(page, group, label, false);
        await Roll(page);

        Assert.IsEmpty(Checked(page, group));
        Assert.IsEmpty(page.FormDialog().Alerts());
        Assert.HasCount(2, SlotActivities(page));
    }

    [TestMethod]
    [DataRow("Filter by games")]
    [DataRow("Filter by themes")]
    [DataRow("Filter by holidays")]
    public async Task ASeriesWhoseIdsOfAGroupAreAllUnknown_SeedsThatFilterOff(string group)
    {
        raidSeries.Filters = new SeriesFilters
        {
            GameIds = group == "Filter by games" ? [goneGame] : [],
            ThemeIds = group == "Filter by themes" ? [goneTheme] : [],
            HolidayIds = group == "Filter by holidays" ? [goneHoliday] : [],
            PlayersTonight = 4,
        };
        var page = await Seeded();

        Assert.IsEmpty(Checked(page, group));
        await Roll(page);

        Assert.IsEmpty(page.FormDialog().Alerts());
        Assert.HasCount(2, SlotActivities(page), "the roll did not take the Series' slot count");
    }

    // #249: a library with no Theme (or no Holiday) shows no checkbox in that group, so a Series id of it could never be
    // cleared: the group seeds off, and the roll is not left with an id no Activity carries.
    [TestMethod]
    [DataRow("Filter by themes")]
    [DataRow("Filter by holidays")]
    public async Task ASeriesSeededWhileItsGroupsLibraryListIsEmpty_SeedsThatFilterOff(string group)
    {
        if (group == "Filter by themes")
        {
            api.Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>());
            raidSeries.Filters.ThemeIds = [goneTheme];
        }
        else
        {
            api.Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>());
            raidSeries.Filters.HolidayIds = [goneHoliday];
        }
        var page = await Seeded();

        Assert.IsEmpty(Checked(page, group));
        await Roll(page);

        Assert.IsEmpty(page.FormDialog().Alerts());
        Assert.HasCount(2, SlotActivities(page), "the roll did not take the Series' slot count");
    }

    // #249: a library list that failed to load is not there when the Series read seeds the dialog, so none of that
    // group's ids could be shown: the group seeds off, and stays off once Retry loads the list.
    [TestMethod]
    [DataRow("Filter by games", "/api/games")]
    [DataRow("Filter by themes", "/api/themes")]
    [DataRow("Filter by holidays", "/api/holidays")]
    public async Task ASeriesSeededBeforeItsGroupsLibraryListLoaded_SeedsThatFilterOff(string group, string list)
    {
        api.Status(HttpMethod.Get, list, HttpStatusCode.InternalServerError);
        var page = await Seeded();
        if (list == "/api/games") api.Json(HttpMethod.Get, list, new[] { alpha, beta, gamma });
        else if (list == "/api/themes") api.Json(HttpMethod.Get, list, new[] { spooky, cozy });
        else api.Json(HttpMethod.Get, list, new[] { halloween, yule });

        await page.Button("Retry").ClickAsync(new());

        page.WaitForAssertion(() => Assert.IsNotEmpty(page.FormDialog().QuerySelectorAll($"[role=group][aria-label='{group}'] fluent-checkbox")));
        Assert.IsEmpty(Checked(page, group), "an id seeded while its list was not loaded is ticked once the list is in");
    }

    // #246 item 4: the Occurrence's own Unique games only (copied from its Series when it was made) is what the dialog
    // uses; the Series' value read now is not seeded.
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task UniqueGamesOnly_StaysTheOccurrencesOwn_WhenItsSeriesDiffers(bool own)
    {
        empty.UniqueGamesOnly = own;
        raidSeries.UniqueGamesOnly = !own;

        var page = await Seeded();

        Assert.AreEqual(own, SwitchOn(page, "Unique games only"));
    }

    // #246 item 5: the dialog has drawn itself before its Series read is in; once it is, the panel shows the template.
    [TestMethod]
    public async Task ASeriesReadThatFinishesAfterTheDialogDrew_IsShown()
    {
        var gate = api.Hold(HttpMethod.Get, SeriesPath(RaidSeries), () => Stored(raidSeries, RaidTag));
        var page = await OpenEdit("Empty night");
        Assert.AreEqual(string.Empty, Box(page, PlayersBox));
        Assert.IsEmpty(Checked(page, "Filter by games"));

        gate.SetResult();

        page.WaitForAssertion(() => Assert.AreEqual("4", Box(page, PlayersBox)));
        CollectionAssert.AreEquivalent(new[] { "Alpha", "Beta" }, Checked(page, "Filter by games"));
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static ActivityEntity Tagged(GameEntity game, string name, ThemeEntity theme, HolidayEntity holiday) =>
        new() { Name = name, GameId = game.Id, ThemeIds = [theme.Id], HolidayIds = [holiday.Id] };

    // The empty Occurrence's edit dialog once its Series read is in.
    private async Task<IRenderedComponent<Events>> Seeded()
    {
        var page = await OpenEdit("Empty night");
        page.WaitForAssertion(() => Assert.AreEqual("4", Box(page, PlayersBox)));
        return page;
    }

    private static Task Tick(IRenderedComponent<Events> page, string group, string label, bool on) =>
        page.FormDialog().QuerySelector($"[role=group][aria-label='{group}']")!.QuerySelectorAll("fluent-checkbox")
            .Single(c => c.TextContent.Trim() == label).TriggerEventAsync("oncheckedchange", new CheckboxChangeEventArgs { Checked = on });
}
