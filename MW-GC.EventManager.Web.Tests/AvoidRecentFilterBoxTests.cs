using System.Globalization;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using MW_GC.EventManager.Web.Services;
using MW_GC.EventManager.Web.Shared.Events;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// #95: the avoid-recent switch and box in the create-and-edit Event dialog's Filters, on the real Events page. The
// library has four Games with one Activity each, so a new Event rolls three slots and, once one Activity is avoided,
// the roll can only be the other three. Two Events lie before today (Older offers Alpha three days ago, Newer offers
// Beta two days ago) and one after (Later offers Gamma in five days); a new Event is planned for today at 19:00.
[TestClass]
public sealed class AvoidRecentFilterBoxTests : IDisposable
{
    private const string SwitchLabel = "Avoid recent";
    private const string BoxLabel = "Recent Events to avoid";
    private const string Message = "Avoid recent must be a whole number from 1 to 10.";
    private const string Hint = "Filters apply when randomizing all selections or re-rolling a slot";
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly GameEntity g1 = new() { Name = "G1" }, g2 = new() { Name = "G2" }, g3 = new() { Name = "G3" }, g4 = new() { Name = "G4" };
    private readonly ActivityEntity alpha, beta, gamma, delta;
    private readonly EventEntity older, newer, later;

    public AvoidRecentFilterBoxTests()
    {
        alpha = new ActivityEntity { Name = "Alpha", GameId = g1.Id };
        beta = new ActivityEntity { Name = "Beta", GameId = g2.Id };
        gamma = new ActivityEntity { Name = "Gamma", GameId = g3.Id };
        delta = new ActivityEntity { Name = "Delta", GameId = g4.Id };
        older = new EventEntity { Name = "Older", Date = DaysFromToday(-3), Selections = [Pick(g1, alpha)] };
        newer = new EventEntity { Name = "Newer", Date = DaysFromToday(-2), Selections = [Pick(g2, beta)] };
        later = new EventEntity { Name = "Later", Date = DaysFromToday(5), Selections = [Pick(g3, gamma)] };
        api.Json(HttpMethod.Get, "/api/games", new[] { g1, g2, g3, g4 })
           .Json(HttpMethod.Get, "/api/activities", new[] { alpha, beta, gamma, delta })
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>())
           .Json(HttpMethod.Get, "/api/events", new[] { later, older, newer });
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    // ---- on ------------------------------------------------------------------------------------

    // Switched on, N is 3: Newer and Older are recent (Later is after today), which leaves two Activities for three
    // slots, so the roll keeps the slots and names the Filter.
    [TestMethod]
    public async Task SwitchingItOn_ShowsThree_AndRollsAvoidTheRecentActivities()
    {
        var page = await OpenCreate();
        var before = SlotActivities(page);

        await SwitchTo(page, true);

        Assert.AreEqual("3", BoxValue(page));
        Assert.IsTrue(IsOn(page));
        Assert.Contains(Hint, page.FormDialog().TextContent);
        await Roll(page);
        Assert.AreEqual(SlotPlanner.AvoidRecentSelectionsKeptError, Assert.ContainsSingle(page.FormDialog().Alerts()));
        Assert.AreSequenceEqual(before.ToArray(), SlotActivities(page).ToArray());
    }

    [TestMethod]
    [DataRow("1")]
    [DataRow(" 1 ")]
    public async Task AValidN_TurnsItOn_AndEveryRollSkipsTheRecentActivities(string text)
    {
        var page = await OpenCreate();

        await Type(page, text);

        Assert.IsTrue(IsOn(page));
        Assert.IsEmpty(page.FormDialog().Alerts());
        for (var i = 0; i < 15; i++)
        {
            await Roll(page);
            CollectionAssert.AreEquivalent(Ids(alpha, gamma, delta), SlotActivities(page), $"roll {i}");
        }
    }

    // ---- off -----------------------------------------------------------------------------------

    // A valid N first (3, which leaves too few Activities to roll), so a bad value that kept or used it would fail
    // the roll; with the Filter off the roll fills all three slots.
    [TestMethod]
    [DataRow("abc")]
    [DataRow("0")]
    [DataRow("-1")]
    [DataRow("11")]
    [DataRow("2.5")]
    [DataRow("2.0")]
    [DataRow("1e1")]
    [DataRow("1,0")]
    [DataRow("(3)")]
    public async Task ABadN_ShowsTheBoxsMessage_AndLeavesItOff(string text)
    {
        var page = await OpenCreate();
        await SwitchTo(page, true);

        await Type(page, text);

        Assert.AreEqual(Message, Assert.ContainsSingle(page.FormDialog().Alerts()));
        Assert.AreEqual(Message, Assert.ContainsSingle(Box(page).ParentElement!.Alerts()));
        Assert.AreEqual("true", Box(page).GetAttribute("aria-invalid"));
        Assert.AreEqual(text, BoxValue(page));
        Assert.IsFalse(IsOn(page));
        Assert.DoesNotContain(Hint, page.FormDialog().TextContent);
        await Roll(page);
        Assert.AreEqual(Message, Assert.ContainsSingle(page.FormDialog().Alerts()));
        Assert.HasCount(3, SlotActivities(page));

        // Fixing it turns the Filter on and the message goes.
        await Type(page, "1");
        Assert.IsEmpty(page.FormDialog().Alerts());
        Assert.IsNull(Box(page).GetAttribute("aria-invalid"));
        Assert.IsTrue(IsOn(page));
    }

    [TestMethod]
    public async Task SwitchingItOff_EmptiesTheBox_AndRollsAreUnrestricted()
    {
        var page = await OpenCreate();
        await SwitchTo(page, true);

        await SwitchTo(page, false);

        Assert.AreEqual(string.Empty, BoxValue(page));
        Assert.IsFalse(IsOn(page));
        Assert.DoesNotContain(Hint, page.FormDialog().TextContent);
        await Roll(page);
        Assert.IsEmpty(page.FormDialog().Alerts());
        Assert.HasCount(3, SlotActivities(page));
    }

    // ---- the planned date ----------------------------------------------------------------------

    // With N 1 today, Newer is the recent Event. Moved past Later, the dialog's date makes Later the recent Event.
    [TestMethod]
    public async Task MovingTheDate_RecomputesTheRecentEvents()
    {
        var page = await OpenCreate();
        await Type(page, "1");
        await Roll(page);
        CollectionAssert.AreEquivalent(Ids(alpha, gamma, delta), SlotActivities(page));

        await TypeDate(page, DateTime.Today.AddDays(10));

        for (var i = 0; i < 10; i++)
        {
            await Roll(page);
            CollectionAssert.AreEquivalent(Ids(alpha, beta, delta), SlotActivities(page), $"roll {i}");
        }
    }

    // Moved before every Event, nothing is recent: N 3 no longer leaves too few Activities.
    [TestMethod]
    public async Task MovingTheDateBeforeEveryEvent_LeavesNothingRecent()
    {
        var page = await OpenCreate();
        await SwitchTo(page, true);

        await TypeDate(page, DateTime.Today.AddDays(-30));

        await Roll(page);
        Assert.IsEmpty(page.FormDialog().Alerts());
        Assert.HasCount(3, SlotActivities(page));
    }

    // The time counts too: Today at 19:00 makes an Event earlier today recent; moved to 06:00, it is not.
    [TestMethod]
    public async Task MovingTheTime_RecomputesTheRecentEvents()
    {
        var morning = new EventEntity { Name = "Morning", Date = new DateTimeOffset(DateTime.Today.AddHours(12)), Selections = [Pick(g4, delta)] };
        api.Json(HttpMethod.Get, "/api/events", new[] { later, older, newer, morning });
        var page = await OpenCreate();
        await Type(page, "1");
        await Roll(page);
        CollectionAssert.AreEquivalent(Ids(alpha, beta, gamma), SlotActivities(page));

        await TimeBox(page).InputAsync(new ChangeEventArgs { Value = "06:00" });

        await Roll(page);
        CollectionAssert.AreEquivalent(Ids(alpha, gamma, delta), SlotActivities(page));
    }

    // ---- the edited Event ----------------------------------------------------------------------

    // Newer is edited and moved to tomorrow. Not counting itself, the last Event before tomorrow is Older, so its one
    // slot can only roll Beta, its own Activity; counting itself it would roll Alpha.
    [TestMethod]
    public async Task EditingAnEvent_DoesNotCountItAgainstItself()
    {
        api.Json(HttpMethod.Get, "/api/activities", new[] { alpha, beta });
        var page = await OpenPage();
        await RowButton(page, "Newer", "Edit").ClickAsync(new());
        Assert.IsTrue(page.HasFormDialog());
        await Type(page, "1");

        await TypeDate(page, DateTime.Today.AddDays(1));

        for (var i = 0; i < 5; i++)
        {
            await Roll(page);
            Assert.AreSequenceEqual(new[] { beta.Id.ToString() }, SlotActivities(page).ToArray(), $"roll {i}");
        }
    }

    // ---- accessible names ----------------------------------------------------------------------

    // The box is found through its visible label, never by the name being checked.
    [TestMethod]
    public async Task TheSwitchAndTheBox_HaveTheirAccessibleNames_AndTheBoxNamesItsMessage()
    {
        var page = await OpenCreate();

        Assert.ContainsSingle(page.FormDialog().QuerySelectorAll("fluent-switch").Where(s => s.GetAttribute("aria-label") == SwitchLabel));
        var label = page.FormDialog().QuerySelectorAll("label").Single(l => l.TextContent.Trim() == BoxLabel);
        Assert.AreEqual("filter-avoid-recent", label.GetAttribute("for"));
        var field = page.FormDialog().QuerySelector("fluent-text-field#filter-avoid-recent")
            ?? throw new AssertFailedException("No box with the id filter-avoid-recent.");
        Assert.AreEqual(BoxLabel, field.GetAttribute("aria-label"));
        Assert.AreEqual("filter-avoid-recent-message", field.GetAttribute("aria-describedby"));
        Assert.AreSame(field.ParentElement, page.Find("#filter-avoid-recent-message").ParentElement);

        await Type(page, "abc");
        Assert.AreEqual(Message, Assert.ContainsSingle(page.Find("#filter-avoid-recent-message").Alerts()));
    }

    [TestMethod]
    public async Task TheBox_LinksItsMessageIntoTheFieldsShadowRoot()
    {
        var module = harness.JSInterop.SetupModule(AvoidRecentFilterBox.Module);

        var page = await OpenCreate();

        page.WaitForAssertion(() => Assert.AreSequenceEqual(new[] { "filter-avoid-recent-message" },
            module.Invocations["link"].Select(i => (string)i.Arguments[0]!).ToArray()));
        Assert.AreEqual("filter-avoid-recent-message", Box(page).GetAttribute("aria-describedby"));
    }

    // ---- unsaved changes and reopening ---------------------------------------------------------

    // As the players tonight and longest duration boxes count, so does this Filter: switching it on is a change,
    // switching it off again leaves the dialog clean. Nothing is ever sent.
    [TestMethod]
    public async Task SwitchingItOn_IsAnUnsavedChange_UntilItIsSwitchedOffAgain()
    {
        var page = await OpenCreate();
        await SwitchTo(page, true);

        await Dismiss(page);
        Assert.IsTrue(page.HasConfirmation(), "switching avoid recent on did not count as a change");
        await page.Confirmation().Button("Keep editing").ClickAsync(new());

        await SwitchTo(page, false);
        await Dismiss(page);
        Assert.IsFalse(page.HasConfirmation());
        Assert.IsFalse(page.HasFormDialog());
        Assert.IsEmpty(api.Requests.Where(r => r.Method != HttpMethod.Get).ToList());
    }

    [TestMethod]
    public async Task TypingABadN_IsAnUnsavedChange()
    {
        var page = await OpenCreate();
        await Type(page, "abc");

        await Dismiss(page);

        Assert.IsTrue(page.HasConfirmation(), "typing in the avoid recent box did not count as a change");
    }

    [TestMethod]
    public async Task ReopeningTheCreateDialog_StartsWithItOff()
    {
        var page = await OpenCreate();
        await Type(page, "abc");
        await page.FormDialog().Button("Cancel").ClickAsync(new());

        await page.Button("Create Event").ClickAsync(new());

        AssertOffAndEmpty(page);
        await Roll(page);
        Assert.HasCount(3, SlotActivities(page));
    }

    [TestMethod]
    public async Task OpeningEdit_AfterSwitchingItOnInACreateDialog_StartsWithItOff()
    {
        var page = await OpenCreate();
        await SwitchTo(page, true);
        await page.FormDialog().Button("Cancel").ClickAsync(new());

        await RowButton(page, "Later", "Edit").ClickAsync(new());

        AssertOffAndEmpty(page);
        // What was switched on in the create dialog is not this dialog's start: nothing changed, so it closes at once.
        await Dismiss(page);
        Assert.IsFalse(page.HasConfirmation());
        Assert.IsFalse(page.HasFormDialog());
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static DateTimeOffset DaysFromToday(int days) => new(DateTime.Today.AddDays(days).AddHours(12));

    private static List<string> Ids(params ActivityEntity[] activities) => activities.Select(a => a.Id.ToString()).ToList();

    private async Task<IRenderedComponent<Events>> OpenPage()
    {
        var page = harness.Render<Events>();
        page.WaitForAssertion(() => Assert.AreEqual(5, api.Requests.Count(r => r.Method == HttpMethod.Get)));
        page.WaitForAssertion(() => Assert.IsTrue(page.Rows().Any(r => r.Contains("Newer"))));
        return page;
    }

    private async Task<IRenderedComponent<Events>> OpenCreate()
    {
        var page = await OpenPage();
        await page.Button("Create Event").ClickAsync(new());
        Assert.IsTrue(page.HasFormDialog());
        AssertOffAndEmpty(page);
        return page;
    }

    private void AssertOffAndEmpty(IRenderedComponent<Events> page)
    {
        Assert.AreEqual(string.Empty, BoxValue(page));
        Assert.IsFalse(IsOn(page));
        Assert.IsEmpty(page.FormDialog().Alerts());
        Assert.DoesNotContain(Hint, page.FormDialog().TextContent);
    }

    private static IElement Switch(IRenderedComponent<Events> page) =>
        page.FormDialog().QuerySelectorAll("fluent-switch").Single(s => s.GetAttribute("aria-label") == SwitchLabel);

    // Fluent renders current-checked only while the switch is on.
    private static bool IsOn(IRenderedComponent<Events> page) => Switch(page).HasAttribute("current-checked");

    // A click (or Space) on a switch makes <fluent-switch> raise "change", handed to the component as
    // "onswitchcheckedchange" (NonTextInputDirtyTests).
    private static Task SwitchTo(IRenderedComponent<Events> page, bool on) =>
        Switch(page).TriggerEventAsync("onswitchcheckedchange", new CheckboxChangeEventArgs { Checked = on });

    private static IElement Box(IRenderedComponent<Events> page) =>
        page.FormDialog().QuerySelector($"fluent-text-field[aria-label='{BoxLabel}']")
            ?? throw new AssertFailedException($"No box named {BoxLabel}.");

    private static string BoxValue(IRenderedComponent<Events> page) => Box(page).GetAttribute("value") ?? string.Empty;

    private static Task Type(IRenderedComponent<Events> page, string text) => Box(page).InputAsync(new ChangeEventArgs { Value = text });

    // A date typed into the date box reaches the page when the box raises "change" (NonTextInputDirtyTests).
    private static async Task TypeDate(IRenderedComponent<Events> page, DateTime date)
    {
        var typed = date.ToString(CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern, CultureInfo.CurrentCulture);
        await page.FormDialog().QuerySelector("fluent-text-field.fluent-datepicker")!.ChangeAsync(new ChangeEventArgs { Value = typed });
        Assert.AreEqual(typed, page.FormDialog().QuerySelector("fluent-text-field.fluent-datepicker")!.GetAttribute("value"));
    }

    private static IElement TimeBox(IRenderedComponent<Events> page) =>
        page.FormDialog().QuerySelector("fluent-text-field.fluent-timepicker")!;

    private static Task Roll(IRenderedComponent<Events> page) => page.FormDialog().Button("Randomize All").ClickAsync(new());

    private static List<IElement> Slots(IRenderedComponent<Events> page) =>
        page.FormDialog().QuerySelectorAll("fluent-card")
            .Where(c => c.QuerySelector("fluent-button[aria-label^='Re-roll slot']") is not null && c.QuerySelectorAll("fluent-card").Length == 0)
            .ToList();

    private static List<string> SlotActivities(IRenderedComponent<Events> page) =>
        Slots(page).Select(s => s.QuerySelectorAll("fluent-select")[1].GetAttribute("current-value")!).ToList();
}
