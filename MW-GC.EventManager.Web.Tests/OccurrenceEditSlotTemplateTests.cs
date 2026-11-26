using System.Net;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using MW_GC.EventManager.Web.Shared.Events;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// #204 story 11, #237 S5: the edit dialog of an Occurrence not rolled yet (no Selections) starts from its Series' slot
// template: the Filters panel shows the Series' Filters and the first Randomize All rolls its slot count. The library
// is built so the Series' Filters leave exactly one Activity in each of four Games (Crypt, Manor, Swamp and Tower); every
// other Activity fails exactly one Filter: Attic's Cozy night (Theme), Yule log (Holiday), Crowd (players), Marathon
// (duration) and Encore (offered in Encore night, a recent Event), and Garden's Outside (Game). With no Filters six Games
// have an Activity, so the default roll is three slots and the Series' four is told apart from both. Fixture:
// OccurrenceEditFixture, whose Raid Series (slot count 4) gets the Filters below.
[TestClass]
public sealed class OccurrenceEditSlotTemplateTests : OccurrenceEditFixture
{
    private const string AvoidBox = "Recent Events to avoid";
    private const string PlayersBox = "Players tonight";
    private const string DurationBox = "Longest duration (minutes)";
    private const string ThemedSwitch = "Themed Activities Only";

    private readonly ThemeEntity spooky = new() { Name = "Spooky" }, cozy = new() { Name = "Cozy" };
    private readonly HolidayEntity halloween = new() { Name = "Halloween" }, yule = new() { Name = "Yule" };
    private readonly GameEntity crypt = new() { Name = "Crypt" }, manor = new() { Name = "Manor" }, swamp = new() { Name = "Swamp" },
        tower = new() { Name = "Tower" }, attic = new() { Name = "Attic" }, garden = new() { Name = "Garden" };
    private readonly List<ActivityEntity> fits;

    public OccurrenceEditSlotTemplateTests()
    {
        fits = [Fit(crypt, "Seance"), Fit(manor, "Ballroom"), Fit(swamp, "Bog walk"), Fit(tower, "Bell climb")];
        var cozyNight = Fit(attic, "Cozy night"); cozyNight.ThemeIds = [cozy.Id];
        var yuleLog = Fit(attic, "Yule log"); yuleLog.HolidayIds = [yule.Id];
        var crowd = Fit(attic, "Crowd"); crowd.MinPlayers = 6;
        var marathon = Fit(attic, "Marathon"); marathon.DurationMinutes = 90;
        var encore = Fit(attic, "Encore");
        var outside = Fit(garden, "Outside");
        // Two days before the empty Occurrence (22 October 19:00 at UTC-04:00): the most recent Event before it.
        var encoreNight = new EventEntity { Name = "Encore night", Date = new DateTimeOffset(2026, 10, 20, 19, 0, 0, RaidOffset), Selections = [Pick(attic, encore)] };
        api.Json(HttpMethod.Get, "/api/games", new[] { crypt, manor, swamp, tower, attic, garden })
           .Json(HttpMethod.Get, "/api/activities", fits.Concat([cozyNight, yuleLog, crowd, marathon, encore, outside]).ToArray())
           .Json(HttpMethod.Get, "/api/themes", new[] { spooky, cozy })
           .Json(HttpMethod.Get, "/api/holidays", new[] { halloween, yule })
           .Json(HttpMethod.Get, "/api/events", new[] { quiz, raid, empty, encoreNight });
        // Every value distinct from the dialog's own start (3 slots, every Filter off) and from each other.
        raidSeries.SlotCount = 4;
        raidSeries.Filters = new SeriesFilters
        {
            ThemedOnly = true, GameIds = [crypt.Id, manor.Id, swamp.Id, tower.Id, attic.Id], ThemeIds = [spooky.Id], HolidayIds = [halloween.Id],
            AvoidRecent = 2, PlayersTonight = 4, LongestDuration = 60,
        };
    }

    [TestMethod]
    public async Task AnUnrolledOccurrence_ShowsItsSeriesSlotTemplate_AndRandomizeAllRollsIt()
    {
        var page = await OpenEdit("Empty night");

        page.WaitForAssertion(() => Assert.AreEqual("4", Box(page, PlayersBox)));
        Assert.AreEqual("60", Box(page, DurationBox));
        Assert.AreEqual("2", Box(page, AvoidBox));
        Assert.IsTrue(SwitchOn(page, "Avoid recent"));
        Assert.IsTrue(SwitchOn(page, ThemedSwitch));
        CollectionAssert.AreEquivalent(new[] { "Attic", "Crypt", "Manor", "Swamp", "Tower" }, Checked(page, "Filter by games"));
        CollectionAssert.AreEqual(new[] { "Spooky" }, Checked(page, "Filter by themes"));
        CollectionAssert.AreEqual(new[] { "Halloween" }, Checked(page, "Filter by holidays"));
        Assert.IsEmpty(SlotActivities(page), "the Occurrence was rolled as it opened");
        Assert.IsEmpty(page.FormDialog().Alerts());

        for (var i = 0; i < 5; i++)
        {
            await Roll(page);
            CollectionAssert.AreEquivalent(Ids(fits), SlotActivities(page), $"roll {i}");
        }
        Assert.IsEmpty(page.FormDialog().Alerts());
    }

    [TestMethod]
    public async Task AnOccurrenceWithSelections_StartsFromNothingSeeded()
    {
        var gate = HoldTheSeries();
        var page = await OpenEdit("Weekly raid");

        await AfterTheRead(page, gate);

        AssertNothingSeeded(page);
        Assert.HasCount(2, SlotActivities(page));
    }

    // The empty Occurrence's read finishes only after its dialog was cancelled and a standalone Event's opened.
    [TestMethod]
    public async Task AStandaloneEvent_OpenedBeforeAnEarlierOccurrencesReadFinishes_StartsFromNothingSeeded()
    {
        var gate = HoldTheSeries();
        var page = await OpenEdit("Empty night");
        await page.FormDialog().Button("Cancel").ClickAsync(new());
        await RowButton(page, "Quiz night", "Edit").ClickAsync(new());

        await AfterTheRead(page, gate);

        Assert.AreEqual("Quiz night", Name(page));
        AssertNothingSeeded(page);
        await Dismiss(page);
        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        Assert.IsFalse(page.HasConfirmation());
    }

    [TestMethod]
    [DataRow(HttpStatusCode.InternalServerError)]
    [DataRow(HttpStatusCode.NotFound)]
    public async Task AFailedSeriesRead_ChangesNothing_AndShowsNoError(HttpStatusCode status)
    {
        var gate = HoldTheSeries(status);
        var page = await OpenEdit("Empty night");

        await AfterTheRead(page, gate);

        AssertNothingSeeded(page);
        Assert.IsEmpty(page.FormDialog().Alerts());
        Assert.IsEmpty(page.PageAlerts());
        await Roll(page);
        Assert.HasCount(3, SlotActivities(page), "the roll did not start from the dialog's own slot count");
        Assert.IsEmpty(page.FormDialog().Alerts());
    }

    [TestMethod]
    public async Task ASeededOccurrence_ClosedWithoutATouch_DoesNotAskToDiscard()
    {
        var page = await OpenEdit("Empty night");
        page.WaitForAssertion(() => Assert.AreEqual("4", Box(page, PlayersBox)));

        await Dismiss(page);

        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        Assert.IsFalse(page.HasConfirmation());
    }

    [TestMethod]
    [DataRow("name")]
    [DataRow("date")]
    [DataRow("filter")]
    [DataRow("slots")]
    public async Task AChangeMadeBeforeTheSeriesReadFinishes_IsKept_AndNothingIsSeeded(string change)
    {
        var gate = HoldTheSeries();
        var page = await OpenEdit("Empty night");
        if (change == "name") await Rename(page, "Raid night");
        else if (change == "date") await TypeDate(page, "10/24/2026");
        else if (change == "filter") await TypeBox(page, PlayersBox, "7");
        else await Roll(page);
        var slots = SlotActivities(page);
        var date = DateBox(page);

        await AfterTheRead(page, gate);

        Assert.AreEqual(change == "name" ? "Raid night" : "Empty night", Name(page));
        Assert.AreEqual(date, DateBox(page));
        if (change == "date") Assert.AreEqual("10/24/2026", date);
        Assert.AreEqual(change == "filter" ? "7" : string.Empty, Box(page, PlayersBox));
        Assert.AreEqual(string.Empty, Box(page, DurationBox));
        Assert.AreEqual(string.Empty, Box(page, AvoidBox));
        Assert.IsFalse(SwitchOn(page, ThemedSwitch));
        Assert.IsEmpty(Checked(page, "Filter by games"));
        Assert.IsEmpty(Checked(page, "Filter by themes"));
        Assert.IsEmpty(Checked(page, "Filter by holidays"));
        CollectionAssert.AreEqual(slots, SlotActivities(page));
    }

    [TestMethod]
    public async Task ASeriesWithNoFilters_SeedsOnlyItsSlotCount()
    {
        raidSeries.Filters = new SeriesFilters();
        var gate = HoldTheSeries();
        var page = await OpenEdit("Empty night");

        await AfterTheRead(page, gate);

        AssertNothingSeeded(page);
        await Roll(page);
        Assert.HasCount(4, SlotActivities(page));
        Assert.IsEmpty(page.FormDialog().Alerts());
    }

    // A Series' slot count is for its own Occurrence's opening only: the next create, or an edit whose read fails,
    // rolls the dialog's own default again.
    [TestMethod]
    [DataRow("create")]
    [DataRow("edit after a failed read")]
    public async Task TheNextOpening_StartsFromTheDialogsOwnDefaultsAgain(string next)
    {
        var page = await OpenEdit("Empty night");
        page.WaitForAssertion(() => Assert.AreEqual("4", Box(page, PlayersBox)));
        await page.FormDialog().Button("Cancel").ClickAsync(new());

        if (next == "create")
        {
            await page.Button("Create Event").ClickAsync(new());
            Assert.AreEqual("Customize Event", page.DialogTitle());
        }
        else
        {
            var gate = HoldTheSeries(HttpStatusCode.InternalServerError);
            await RowButton(page, "Empty night", "Edit").ClickAsync(new());
            await AfterTheRead(page, gate);
            await Roll(page);
        }

        AssertNothingSeeded(page);
        Assert.HasCount(3, SlotActivities(page));
    }

    // Rolling from the template changes the Occurrence's slots, so only This event only is offered, and it sends that
    // Event alone: the Series is never written.
    [TestMethod]
    public async Task ARolledOccurrence_SavedThisEventOnly_SendsOnlyThatEvent()
    {
        var page = await OpenEdit("Empty night");
        page.WaitForAssertion(() => Assert.AreEqual("4", Box(page, PlayersBox)));
        await Roll(page);
        await Update(page);

        CollectionAssert.AreEqual(new[] { "This event only" }, Labels(page));
        await ChooseAndConfirm(page, "This event only");

        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        CollectionAssert.AreEqual(new[] { $"PUT {EventPath(empty)}" }, Sent());
        var sent = JsonSerializer.Deserialize<EventEntity>(Assert.ContainsSingle(api.Bodies(HttpMethod.Put, EventPath(empty)))!, JsonSerializerOptions.Web)!;
        CollectionAssert.AreEquivalent(fits.Select(a => a.Id).ToList(), sent.Selections.Select(s => s.Activity.Id).ToList());
        Assert.AreEqual(0, api.Count(HttpMethod.Put, SeriesPath(RaidSeries)));
    }

    // ---- Helpers -------------------------------------------------------------------------------

    // An Activity of the game that passes every Filter of the Raid Series.
    private ActivityEntity Fit(GameEntity game, string name) => new()
    {
        Name = name, GameId = game.Id, ThemeIds = [spooky.Id], HolidayIds = [halloween.Id], MinPlayers = 2, MaxPlayers = 6, DurationMinutes = 45,
    };

    private static List<string> Ids(IEnumerable<ActivityEntity> activities) => activities.Select(a => a.Id.ToString()).ToList();

    // The Raid Series' read, held until the test releases it: the stored Series, or a failed status.
    private HeldRead HoldTheSeries(HttpStatusCode? failure = null)
    {
        var taken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = api.Hold(HttpMethod.Get, SeriesPath(RaidSeries), () =>
        {
            var answer = new WatchedResponse(failure ?? HttpStatusCode.OK, () => taken.TrySetResult());
            if (failure is not null) return answer;
            var stored = Stored(raidSeries, RaidTag);
            answer.Content = stored.Content;
            answer.Headers.TryAddWithoutValidation("ETag", RaidTag);
            return answer;
        });
        return new HeldRead(gate, taken.Task);
    }

    private sealed record HeldRead(TaskCompletionSource Gate, Task Taken);

    // An answer that says when it is disposed: SeriesService disposes it once it has read the Series (or the failure)
    // from it, just before the dialog's own code sees the result.
    private sealed class WatchedResponse(HttpStatusCode status, Action disposed) : HttpResponseMessage(status)
    {
        protected override void Dispose(bool disposing)
        {
            disposed();
            base.Dispose(disposing);
        }
    }

    // Releases the held read and waits until the dialog has taken it in. The answer is read on the page's renderer, and
    // what the dialog does with the result runs in the same turn of it; so once the answer has been read (disposed),
    // one more turn of the renderer, queued from a pool thread, runs after all of that. Nothing here waits for a
    // render, so a dialog that does nothing with the read is waited for just the same.
    private static async Task AfterTheRead(IRenderedComponent<Events> page, HeldRead held)
    {
        held.Gate.SetResult();
        await held.Taken.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Run(() => page.InvokeAsync(() => { }));
    }

    private static void AssertNothingSeeded(IRenderedComponent<Events> page)
    {
        Assert.AreEqual(string.Empty, Box(page, PlayersBox));
        Assert.AreEqual(string.Empty, Box(page, DurationBox));
        Assert.AreEqual(string.Empty, Box(page, AvoidBox));
        Assert.IsFalse(SwitchOn(page, "Avoid recent"));
        Assert.IsFalse(SwitchOn(page, ThemedSwitch));
        Assert.IsEmpty(Checked(page, "Filter by games"));
        Assert.IsEmpty(Checked(page, "Filter by themes"));
        Assert.IsEmpty(Checked(page, "Filter by holidays"));
    }

    private static string Name(IRenderedComponent<Events> page) =>
        page.FormDialog().QuerySelector("fluent-text-field[placeholder='Enter event name...']")!.GetAttribute("value") ?? string.Empty;

    private static string DateBox(IRenderedComponent<Events> page) =>
        page.FormDialog().QuerySelector("fluent-text-field.fluent-datepicker")!.GetAttribute("value") ?? string.Empty;

    private static Task TypeBox(IRenderedComponent<Events> page, string label, string text) =>
        TextBox(page, label).InputAsync(new ChangeEventArgs { Value = text });

}
