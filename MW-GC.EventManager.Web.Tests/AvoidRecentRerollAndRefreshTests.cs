using System.Net;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using MW_GC.EventManager.Web.Services;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// #95, on the real Events page: the slot's Re-roll button names the avoid-recent Filter only when it is the cause, an
// empty or blank N turns the Filter off without a message, and an Events list the page reads again while the dialog
// is open moves the recent Events with it. The library has four Games with one Activity each. Four Events lie before
// today: Oldest offers Delta four days ago, Older Alpha three days ago, Newer Beta two days ago and Yesterday Gamma.
[TestClass]
public sealed class AvoidRecentRerollAndRefreshTests : IDisposable
{
    private const string SwitchLabel = "Avoid recent";
    private const string BoxLabel = "Recent Events to avoid";
    private const string RerollNoMatch = "No alternative matches the current filters";
    private const string RerollTitle = "Re-roll this slot";
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly GameEntity g1 = new() { Name = "G1" }, g2 = new() { Name = "G2" }, g3 = new() { Name = "G3" }, g4 = new() { Name = "G4" };
    private readonly ActivityEntity alpha, beta, gamma, delta;
    private readonly EventEntity oldest, older, newer, yesterday;

    public AvoidRecentRerollAndRefreshTests()
    {
        alpha = new ActivityEntity { Name = "Alpha", GameId = g1.Id };
        beta = new ActivityEntity { Name = "Beta", GameId = g2.Id };
        gamma = new ActivityEntity { Name = "Gamma", GameId = g3.Id };
        delta = new ActivityEntity { Name = "Delta", GameId = g4.Id };
        oldest = new EventEntity { Name = "Oldest", Date = DaysFromToday(-4), Selections = [Pick(g4, delta)] };
        older = new EventEntity { Name = "Older", Date = DaysFromToday(-3), Selections = [Pick(g1, alpha)] };
        newer = new EventEntity { Name = "Newer", Date = DaysFromToday(-2), Selections = [Pick(g2, beta)] };
        yesterday = new EventEntity { Name = "Yesterday", Date = DaysFromToday(-1), Selections = [Pick(g3, gamma)] };
        api.Json(HttpMethod.Get, "/api/games", new[] { g1, g2, g3, g4 })
           .Json(HttpMethod.Get, "/api/activities", new[] { alpha, beta, gamma, delta })
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>())
           .Json(HttpMethod.Get, "/api/events", new[] { oldest, older, newer, yesterday });
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    // ---- the Re-roll button's title ------------------------------------------------------------

    // Yesterday is edited: its one slot holds Gamma, and the three Events before it offer every other Activity. With
    // N 3 every alternative is recent, so the button is disabled and its title names the Filter.
    [TestMethod]
    public async Task WhenEveryAlternativeIsRecent_TheRerollButtonIsDisabled_AndItsTitleNamesTheFilter()
    {
        var page = await OpenEdit("Yesterday");
        Assert.IsFalse(RerollButton(page).HasAttribute("disabled"), "nothing is recent yet, so the slot can be re-rolled");
        Assert.AreEqual(RerollTitle, RerollButton(page).GetAttribute("title"));

        await SwitchTo(page, SwitchLabel, true);

        Assert.AreEqual("3", Box(page).GetAttribute("value"));
        Assert.IsTrue(RerollButton(page).HasAttribute("disabled"));
        Assert.AreEqual(SlotPlanner.RerollAvoidRecentReason, RerollButton(page).GetAttribute("title"));
    }

    // With N 1 only Newer is recent, so Beta is avoided but Alpha and Delta are not: the slot can still be re-rolled,
    // and the enabled button keeps its plain title although avoid recent is on.
    [TestMethod]
    public async Task WhenAnAlternativeIsNotRecent_TheRerollButtonIsEnabled_AndKeepsItsPlainTitle()
    {
        var page = await OpenEdit("Yesterday");
        await SwitchTo(page, SwitchLabel, true);
        Assert.IsTrue(RerollButton(page).HasAttribute("disabled"), "with N 3 every alternative is recent");

        await Box(page).InputAsync(new ChangeEventArgs { Value = "1" });

        Assert.IsTrue(Switch(page, SwitchLabel).HasAttribute("current-checked"), "avoid recent stays on");
        Assert.IsFalse(RerollButton(page).HasAttribute("disabled"));
        Assert.AreEqual(RerollTitle, RerollButton(page).GetAttribute("title"));
    }

    // Themed activities only leaves nothing even without avoid recent (no Activity has a Theme or Holiday), so the
    // disabled button keeps the old title although avoid recent is on too.
    [TestMethod]
    public async Task WhenAnotherFilterBlocksTheReroll_TheRerollButtonKeepsTheOldTitle()
    {
        var page = await OpenEdit("Yesterday");
        await SwitchTo(page, SwitchLabel, true);

        await SwitchTo(page, "Themed Activities Only", true);

        Assert.IsTrue(RerollButton(page).HasAttribute("disabled"));
        Assert.AreEqual(RerollNoMatch, RerollButton(page).GetAttribute("title"));
    }

    // ---- an empty or blank N -------------------------------------------------------------------

    // As the players tonight and longest duration boxes treat it (#217): empty or blank text is off, with no message.
    // N 3 first leaves too few Activities for three slots, so a blank N that kept it would fail the roll.
    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("\t")]
    public async Task AnEmptyOrBlankN_TurnsItOff_WithNoMessage(string text)
    {
        var page = await OpenCreate();
        await SwitchTo(page, SwitchLabel, true);
        await Roll(page);
        Assert.AreEqual(SlotPlanner.AvoidRecentSelectionsKeptError, Assert.ContainsSingle(page.FormDialog().Alerts()));

        await Box(page).InputAsync(new ChangeEventArgs { Value = text });

        Assert.AreEqual(text, Box(page).GetAttribute("value") ?? string.Empty);
        Assert.IsFalse(Switch(page, SwitchLabel).HasAttribute("current-checked"));
        Assert.IsNull(Box(page).GetAttribute("aria-invalid"));
        await Roll(page);
        Assert.IsEmpty(page.FormDialog().Alerts());
        Assert.HasCount(3, SlotActivities(page));
    }

    // ---- the page reads its Events again -------------------------------------------------------

    // A Delete still runs when Create Event is pressed, so the dialog opens with the old list: with N 1, Yesterday is
    // the recent Event and Gamma is avoided. The delete's refresh reads a list in which someone else has added Fresh
    // (Beta, earlier today); from then on Fresh is the recent Event, so Beta is avoided and Gamma rolls again.
    [TestMethod]
    public async Task WhenThePageReadsItsEventsAgain_WhileTheDialogIsOpen_TheRecentEventsMoveWithIt()
    {
        var fresh = new EventEntity { Name = "Fresh", Date = DaysFromToday(0), Selections = [Pick(g2, beta)] };
        var gate = api.Hold(HttpMethod.Delete, $"/api/events/{oldest.Id}", () => new HttpResponseMessage(HttpStatusCode.NoContent));
        var page = await OpenPage();
        await RowButton(page, "Oldest", "Delete").ClickAsync(new());
        var deleting = page.ConfirmAsync();
        page.WaitForAssertion(() => Assert.IsFalse(page.HasConfirmation()));
        api.Json(HttpMethod.Get, "/api/events", new[] { older, newer, yesterday, fresh });

        await page.Button("Create Event").ClickAsync(new());
        await Box(page).InputAsync(new ChangeEventArgs { Value = "1" });
        await Roll(page);
        CollectionAssert.AreEquivalent(Ids(alpha, beta, delta), SlotActivities(page));

        gate.SetResult();
        await deleting;
        page.WaitForAssertion(() => Assert.IsTrue(page.Rows().Any(r => r.Contains("Fresh")), "the page did not read its Events again"));
        Assert.IsTrue(page.HasFormDialog(), "the dialog stays open");

        for (var i = 0; i < 10; i++)
        {
            await Roll(page);
            CollectionAssert.AreEquivalent(Ids(alpha, gamma, delta), SlotActivities(page), $"roll {i}");
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static DateTimeOffset DaysFromToday(int days) => new(DateTime.Today.AddDays(days).AddHours(12));

    private static List<string> Ids(params ActivityEntity[] activities) => activities.Select(a => a.Id.ToString()).ToList();

    private async Task<IRenderedComponent<Events>> OpenPage()
    {
        var page = harness.Render<Events>();
        page.WaitForAssertion(() => Assert.AreEqual(5, api.Requests.Count(r => r.Method == HttpMethod.Get)));
        page.WaitForAssertion(() => Assert.IsTrue(page.Rows().Any(r => r.Contains("Yesterday"))));
        return page;
    }

    private async Task<IRenderedComponent<Events>> OpenCreate()
    {
        var page = await OpenPage();
        await page.Button("Create Event").ClickAsync(new());
        Assert.IsTrue(page.HasFormDialog());
        return page;
    }

    private async Task<IRenderedComponent<Events>> OpenEdit(string name)
    {
        var page = await OpenPage();
        await RowButton(page, name, "Edit").ClickAsync(new());
        Assert.IsTrue(page.HasFormDialog());
        return page;
    }

    private static IElement Switch(IRenderedComponent<Events> page, string label) =>
        page.FormDialog().QuerySelectorAll("fluent-switch").Single(s => s.GetAttribute("aria-label") == label);

    // A click (or Space) on a switch makes <fluent-switch> raise "change", handed to the component as
    // "onswitchcheckedchange" (NonTextInputDirtyTests).
    private static Task SwitchTo(IRenderedComponent<Events> page, string label, bool on) =>
        Switch(page, label).TriggerEventAsync("onswitchcheckedchange", new CheckboxChangeEventArgs { Checked = on });

    private static IElement Box(IRenderedComponent<Events> page) =>
        page.FormDialog().QuerySelector($"fluent-text-field[aria-label='{BoxLabel}']")
            ?? throw new AssertFailedException($"No box named {BoxLabel}.");

    private static IElement RerollButton(IRenderedComponent<Events> page) =>
        page.FormDialog().QuerySelector("fluent-button[aria-label='Re-roll slot 1']")
            ?? throw new AssertFailedException("No Re-roll button for slot 1.");

    private static Task Roll(IRenderedComponent<Events> page) => page.FormDialog().Button("Randomize All").ClickAsync(new());

    private static List<string> SlotActivities(IRenderedComponent<Events> page) =>
        page.FormDialog().QuerySelectorAll("fluent-card")
            .Where(c => c.QuerySelector("fluent-button[aria-label^='Re-roll slot']") is not null && c.QuerySelectorAll("fluent-card").Length == 0)
            .Select(s => s.QuerySelectorAll("fluent-select")[1].GetAttribute("current-value")!).ToList();
}
