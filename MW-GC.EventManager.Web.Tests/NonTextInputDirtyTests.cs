using System.Globalization;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Pages;

namespace MW_GC.EventManager.Web.Tests;

// Issue #91: the "Discard changes?" check for the dialogs' non-text inputs. Each input is changed
// through the event its Fluent UI 4.14.4 component really handles, never by setting a value:
//   checkbox  "oncheckedchange" (the browser "change" of <fluent-checkbox>, raised on click or Space)
//   switch    "onswitchcheckedchange" (the browser "change" of <fluent-switch>, raised on click or Space)
//   select    a click on the <fluent-option>, which is what both the mouse and the keyboard path call
//   date      a click on a day of the calendar the date picker opens, or a typed date once the text
//             box raises "change" (Enter or leaving the box; see Observations in PR for #91)
//   time      "input" on the time box, which counts at once because the picker is Immediate (#89)
// Overlay click and Escape reach a page as "ondialogdismiss", exactly as in DialogSafetyTests.
[TestClass]
public sealed class NonTextInputDirtyTests : IDisposable
{
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly GameEntity kart = new() { Name = "Kart" };
    private readonly GameEntity brawl = new() { Name = "Brawl" };
    private readonly GameEntity puzzle = new() { Name = "Puzzle" };
    private readonly GameEntity quiz = new() { Name = "Quiz" };
    private readonly ThemeEntity spooky = new() { Name = "Spooky" };
    private readonly ThemeEntity cozy = new() { Name = "Cozy" };
    private readonly HolidayEntity halloween = new() { Name = "Halloween" };
    private readonly HolidayEntity winter = new() { Name = "Winter" };
    private readonly ActivityEntity grandPrix;
    private readonly ActivityEntity timeTrial;
    private readonly ActivityEntity battle;
    private readonly ActivityEntity stock;
    private readonly ActivityEntity stamina;
    private readonly ActivityEntity tiles;
    private readonly ActivityEntity trivia;
    private readonly EventEntity night;

    public NonTextInputDirtyTests()
    {
        // Grand Prix already carries two themes and two holidays, so unticking one and ticking it
        // again changes the order of its list but not what is in it.
        grandPrix = new ActivityEntity
        {
            Name = "Grand Prix", GameId = kart.Id,
            ThemeIds = [spooky.Id, cozy.Id], HolidayIds = [halloween.Id, winter.Id]
        };
        timeTrial = new ActivityEntity { Name = "Time Trial", GameId = kart.Id };
        battle = new ActivityEntity { Name = "Battle", GameId = kart.Id };
        stock = new ActivityEntity { Name = "Stock", GameId = brawl.Id };
        stamina = new ActivityEntity { Name = "Stamina", GameId = brawl.Id };
        tiles = new ActivityEntity { Name = "Tiles", GameId = puzzle.Id };
        trivia = new ActivityEntity { Name = "Trivia", GameId = quiz.Id };
        night = new EventEntity
        {
            Name = "Game night",
            Date = new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero),
            UniqueGamesOnly = false,
            Selections = [Pick(kart, grandPrix), Pick(brawl, stock), Pick(kart, timeTrial)]
        };
        api.Json(HttpMethod.Get, "/api/games", new[] { kart, brawl, puzzle, quiz })
           .Json(HttpMethod.Get, "/api/activities", new[] { grandPrix, timeTrial, battle, stock, stamina, tiles, trivia })
           .Json(HttpMethod.Get, "/api/themes", new[] { spooky, cozy })
           .Json(HttpMethod.Get, "/api/holidays", new[] { halloween, winter })
           .Json(HttpMethod.Get, "/api/events", new[] { night });
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    private static Selection Pick(GameEntity game, ActivityEntity act) => new()
    {
        Game = new Game { Id = game.Id, Name = game.Name },
        Activity = new Activity { Id = act.Id, GameId = act.GameId, Name = act.Name }
    };

    // Every non-text input of every dialog that has one. The Activities Game picker is disabled
    // when editing, and the Games, Themes and Holidays dialogs only have text fields.
    private static readonly string[] EventInputs =
        ["date", "date-typed", "themed-only", "game-filter", "theme-filter", "holiday-filter", "unique-games", "slot-game", "slot-activity"];

    public static IEnumerable<object[]> ChangedInputs =>
    [
        ["activities-create", "game"], ["activities-create", "theme"], ["activities-create", "holiday"],
        ["activities-edit", "theme"], ["activities-edit", "holiday"],
        .. EventInputs.Select(i => new object[] { "events-create", i }),
        .. EventInputs.Select(i => new object[] { "events-edit", i })
    ];

    // The same inputs, plus the time picker, typed over or cleared and typed again. The Add Activity
    // Game picker starts with no game and offers no way back to none, so it has no revert case.
    private static readonly string[] EventRevertInputs = [.. EventInputs, "time", "time-cleared"];

    public static IEnumerable<object[]> RevertedInputs =>
    [
        ["activities-create", "theme"], ["activities-create", "holiday"],
        ["activities-edit", "theme"], ["activities-edit", "holiday"],
        .. EventRevertInputs.Select(i => new object[] { "events-create", i }),
        .. EventRevertInputs.Select(i => new object[] { "events-edit", i })
    ];

    [TestMethod]
    [DynamicData(nameof(ChangedInputs))]
    public async Task Dismiss_RightAfterChangingInput_AsksToDiscard(string dialog, string input)
    {
        var page = await OpenDialog(dialog);

        await Change(page, input);
        await Dismiss(page);

        Assert.IsTrue(page.HasFormDialog(), $"{dialog} {input}: Escape closed the dialog and lost the change");
        Assert.IsTrue(page.HasConfirmation(), $"{dialog} {input}: Escape did not ask");
        Assert.AreEqual("Discard changes?", Heading(page.Confirmation()));
        await page.ConfirmAsync("Discard");
        Assert.IsFalse(page.HasFormDialog());
        AssertNoWrites();
    }

    [TestMethod]
    [DynamicData(nameof(RevertedInputs))]
    public async Task Dismiss_AfterChangingInputBackToItsOriginalValue_ClosesImmediately(string dialog, string input)
    {
        var page = await OpenDialog(dialog);

        var revert = await Change(page, input);
        // The change on its own counts, so the revert below is what makes the dialog clean again.
        await Dismiss(page);
        Assert.IsTrue(page.HasConfirmation(), $"{dialog} {input}: the change itself did not count");
        await page.Confirmation().Button("Keep editing").ClickAsync(new());
        await revert();
        await Dismiss(page);

        Assert.IsFalse(page.HasConfirmation(), $"{dialog} {input}: changing the value back still asked to discard");
        Assert.IsFalse(page.HasFormDialog());
        AssertNoWrites();
    }

    // These dialogs open with ticked boxes and filled pickers here; left alone they still close at once.
    [TestMethod]
    [DataRow("activities-create")]
    [DataRow("activities-edit")]
    [DataRow("events-create")]
    [DataRow("events-edit")]
    public async Task Dismiss_Untouched_ClosesImmediately(string dialog)
    {
        var page = await OpenDialog(dialog);

        await Dismiss(page);

        Assert.IsFalse(page.HasConfirmation());
        Assert.IsFalse(page.HasFormDialog());
        AssertNoWrites();
    }

    // Opening the date picker's calendar and closing it again without picking a day changes nothing.
    [TestMethod]
    [DataRow("events-create")]
    [DataRow("events-edit")]
    public async Task Dismiss_AfterOpeningAndClosingDateCalendar_ClosesImmediately(string dialog)
    {
        var page = await OpenDialog(dialog);

        await DateBox(page).ClickAsync(new());
        Assert.IsNotEmpty(Days(page));
        await DateBox(page).ClickAsync(new());
        Assert.IsEmpty(Days(page));
        await Dismiss(page);

        Assert.IsFalse(page.HasConfirmation());
        Assert.IsFalse(page.HasFormDialog());
        AssertNoWrites();
    }

    // ---- Opening a dialog ----------------------------------------------------------------------

    private async Task<IRenderedComponent<IComponent>> OpenDialog(string dialog)
    {
        IRenderedComponent<IComponent> page = dialog.StartsWith("activities") ? harness.Render<Activities>() : harness.Render<Events>();
        var row = dialog.StartsWith("activities") ? "Grand Prix" : "Game night";
        page.WaitForAssertion(() => Assert.IsTrue(page.Rows().Any(r => r.Contains(row))));
        switch (dialog)
        {
            case "activities-create": await page.Button("Add Activity").ClickAsync(new()); break;
            case "activities-edit": await RowButton(page, "Grand Prix", "Edit").ClickAsync(new()); break;
            case "events-create": await page.Button("Create Event").ClickAsync(new()); break;
            case "events-edit": await RowButton(page, "Game night", "Edit").ClickAsync(new()); break;
            default: throw new ArgumentOutOfRangeException(nameof(dialog), dialog);
        }
        Assert.IsTrue(page.HasFormDialog());
        Assert.IsFalse(page.HasConfirmation());
        return page;
    }

    // ---- Changing one input the way its control raises it ---------------------------------------

    // Changes one input and returns how to put it back to the value it had when the dialog opened.
    private async Task<Func<Task>> Change(IRenderedComponent<IComponent> page, string input)
    {
        switch (input)
        {
            case "theme":
            case "theme-filter":
                return await ToggleCheckbox(page, "Spooky");
            case "holiday":
            case "holiday-filter":
                return await ToggleCheckbox(page, "Halloween");
            case "game-filter":
                return await ToggleCheckbox(page, "Kart");
            case "themed-only":
                return await ToggleSwitch(page, "Themed Activities Only");
            case "unique-games":
                return await ToggleSwitch(page, "Unique games only");
            case "game":
                await ActivityGame(page).QuerySelectorAll("fluent-option")
                    .Single(o => o.GetAttribute("value") == brawl.Id.ToString()).ClickAsync(new());
                return () => throw new NotSupportedException("The Add Activity Game picker has no way back to no game.");
            case "date":
                return await PickOtherDay(page);
            case "date-typed":
                return await TypeOtherDate(page);
            case "time":
                return await TypeOtherTime(page);
            case "time-cleared":
                return await ClearTime(page);
            case "slot-game":
                return await ChooseOtherSlotGame(page);
            case "slot-activity":
                return await ChooseOtherSlotActivity(page);
            default:
                throw new ArgumentOutOfRangeException(nameof(input), input);
        }
    }

    // A click (or Space) on a checkbox makes <fluent-checkbox> raise "change", which Fluent UI hands
    // to the component as "oncheckedchange" with the new checked state.
    private static async Task<Func<Task>> ToggleCheckbox(IRenderedComponent<IComponent> page, string label)
    {
        var was = IsChecked(Checkbox(page, label));
        await Checkbox(page, label).TriggerEventAsync("oncheckedchange", new CheckboxChangeEventArgs { Checked = !was });
        Assert.AreEqual(!was, IsChecked(Checkbox(page, label)), $"checkbox {label} did not change");
        return async () =>
        {
            await Checkbox(page, label).TriggerEventAsync("oncheckedchange", new CheckboxChangeEventArgs { Checked = was });
            Assert.AreEqual(was, IsChecked(Checkbox(page, label)));
        };
    }

    // A click (or Space) on a switch makes <fluent-switch> raise "change", handed to the component
    // as "onswitchcheckedchange".
    private static async Task<Func<Task>> ToggleSwitch(IRenderedComponent<IComponent> page, string label)
    {
        var was = IsChecked(Switch(page, label));
        await Switch(page, label).TriggerEventAsync("onswitchcheckedchange", new CheckboxChangeEventArgs { Checked = !was });
        Assert.AreEqual(!was, IsChecked(Switch(page, label)), $"switch {label} did not change");
        return async () =>
        {
            await Switch(page, label).TriggerEventAsync("onswitchcheckedchange", new CheckboxChangeEventArgs { Checked = was });
            Assert.AreEqual(was, IsChecked(Switch(page, label)));
        };
    }

    // A click on the date box opens the calendar; a click on a day picks it and closes the calendar.
    private static async Task<Func<Task>> PickOtherDay(IRenderedComponent<IComponent> page)
    {
        var shown = DateBox(page).GetAttribute("value");
        await DateBox(page).ClickAsync(new());
        var original = Days(page).Single(d => d.HasAttribute("selected")).GetAttribute("value")!;
        await Days(page).First(d => d.GetAttribute("value") != original).ClickAsync(new());
        Assert.IsEmpty(Days(page), "picking a day did not close the calendar");
        Assert.AreNotEqual(shown, DateBox(page).GetAttribute("value"), "picking a day did not change the date");
        return async () =>
        {
            await DateBox(page).ClickAsync(new());
            await Days(page).Single(d => d.GetAttribute("value") == original).ClickAsync(new());
            Assert.AreEqual(shown, DateBox(page).GetAttribute("value"));
        };
    }

    // A date typed into the box reaches the page when the box raises "change": on Enter, or when the
    // box loses focus. The date picker has no Immediate option for its box.
    private static async Task<Func<Task>> TypeOtherDate(IRenderedComponent<IComponent> page)
    {
        var shown = DateBox(page).GetAttribute("value")!;
        var typed = new DateTime(2027, 1, 15).ToString(CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern, CultureInfo.CurrentCulture);
        await DateBox(page).ChangeAsync(new ChangeEventArgs { Value = typed });
        Assert.AreEqual(typed, DateBox(page).GetAttribute("value"), "the typed date was not taken");
        return async () =>
        {
            await DateBox(page).ChangeAsync(new ChangeEventArgs { Value = shown });
            Assert.AreEqual(shown, DateBox(page).GetAttribute("value"));
        };
    }

    // The time box is Immediate (#89): each "input" counts before the box loses focus.
    private static async Task<Func<Task>> TypeOtherTime(IRenderedComponent<IComponent> page)
    {
        var shown = TimeBox(page).GetAttribute("current-value")!;
        var typed = shown == "06:15" ? "06:45" : "06:15";
        await TimeBox(page).InputAsync(new ChangeEventArgs { Value = typed });
        Assert.AreEqual(typed, TimeBox(page).GetAttribute("current-value"));
        return async () =>
        {
            await TimeBox(page).InputAsync(new ChangeEventArgs { Value = shown });
            Assert.AreEqual(shown, TimeBox(page).GetAttribute("current-value"));
        };
    }

    // Selecting the time and deleting it raises "input" with an empty value; typing the same time
    // again raises "input" with it.
    private static async Task<Func<Task>> ClearTime(IRenderedComponent<IComponent> page)
    {
        var shown = TimeBox(page).GetAttribute("current-value")!;
        await TimeBox(page).InputAsync(new ChangeEventArgs { Value = "" });
        Assert.IsTrue(string.IsNullOrEmpty(TimeBox(page).GetAttribute("current-value")), "clearing the time did not take");
        return async () =>
        {
            await TimeBox(page).InputAsync(new ChangeEventArgs { Value = shown });
            Assert.AreEqual(shown, TimeBox(page).GetAttribute("current-value"));
        };
    }

    // Picks another Game for the first slot that offers one. Changing the Game also picks one of that
    // Game's activities, so going back means picking the original Game and then its original Activity.
    private static async Task<Func<Task>> ChooseOtherSlotGame(IRenderedComponent<IComponent> page)
    {
        var slot = Enumerable.Range(0, Slots(page).Count).First(i => Alternatives(SlotSelect(page, i, 0)).Count > 0);
        var game = SlotSelect(page, slot, 0).GetAttribute("current-value")!;
        var activity = SlotSelect(page, slot, 1).GetAttribute("current-value")!;
        var other = Alternatives(SlotSelect(page, slot, 0))[0];
        await Choose(page, slot, 0, other);
        Assert.AreEqual(other, SlotSelect(page, slot, 0).GetAttribute("current-value"), "the slot's Game did not change");
        return async () =>
        {
            await Choose(page, slot, 0, game);
            await Choose(page, slot, 1, activity);
            Assert.AreEqual(game, SlotSelect(page, slot, 0).GetAttribute("current-value"));
            Assert.AreEqual(activity, SlotSelect(page, slot, 1).GetAttribute("current-value"));
        };
    }

    private static async Task<Func<Task>> ChooseOtherSlotActivity(IRenderedComponent<IComponent> page)
    {
        var slot = Enumerable.Range(0, Slots(page).Count).First(i => Alternatives(SlotSelect(page, i, 1)).Count > 0);
        var activity = SlotSelect(page, slot, 1).GetAttribute("current-value")!;
        var other = Alternatives(SlotSelect(page, slot, 1))[0];
        await Choose(page, slot, 1, other);
        Assert.AreEqual(other, SlotSelect(page, slot, 1).GetAttribute("current-value"), "the slot's Activity did not change");
        return async () =>
        {
            await Choose(page, slot, 1, activity);
            Assert.AreEqual(activity, SlotSelect(page, slot, 1).GetAttribute("current-value"));
        };
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private void AssertNoWrites() => Assert.IsEmpty(api.Requests.Where(r => r.Method != HttpMethod.Get).ToList());

    // Overlay click or Escape on the page's form dialog.
    private static Task Dismiss(IRenderedComponent<IComponent> page)
    {
        var dialog = page.FormDialog();
        return dialog.TriggerEventAsync("ondialogdismiss", new DialogEventArgs { Id = dialog.Id, Reason = "dismiss" });
    }

    private static IElement RowButton(IRenderedComponent<IComponent> page, string item, string title) =>
        page.FindAll("tbody tr.fluent-data-grid-row")
            .Where(r => r.Closest("fluent-dialog") is null)
            .Single(r => r.TextContent.Contains(item))
            .QuerySelector($"fluent-button[title={title}]")!;

    private static IElement Checkbox(IRenderedComponent<IComponent> page, string label) =>
        page.FormDialog().QuerySelectorAll("fluent-checkbox").Single(c => c.TextContent.Trim() == label);

    private static IElement Switch(IRenderedComponent<IComponent> page, string label) =>
        page.FormDialog().QuerySelectorAll("fluent-switch").Single(s => s.GetAttribute("aria-label") == label);

    // Fluent renders current-checked only while the box or switch is on.
    private static bool IsChecked(IElement element) => element.HasAttribute("current-checked");

    private static IElement ActivityGame(IRenderedComponent<IComponent> page) =>
        page.FormDialog().QuerySelector("fluent-select")!;

    private static IElement DateBox(IRenderedComponent<IComponent> page) =>
        page.FormDialog().QuerySelector("fluent-text-field.fluent-datepicker")!;

    private static IElement TimeBox(IRenderedComponent<IComponent> page) =>
        page.FormDialog().QuerySelector("fluent-text-field.fluent-timepicker")!;

    // The open calendar's days of the shown month that can be picked.
    private static List<IElement> Days(IRenderedComponent<IComponent> page) =>
        page.FormDialog().QuerySelectorAll("div[part=day][role=button]").ToList();

    private static List<IElement> Slots(IRenderedComponent<IComponent> page) =>
        page.FormDialog().QuerySelectorAll("fluent-card")
            .Where(c => c.QuerySelector("fluent-button[aria-label^='Re-roll slot']") is not null
                        && c.QuerySelectorAll("fluent-card").Length == 0)
            .ToList();

    // picker 0 is the slot's Game, 1 its Activity.
    private static IElement SlotSelect(IRenderedComponent<IComponent> page, int slot, int picker) =>
        Slots(page)[slot].QuerySelectorAll("fluent-select")[picker];

    private static List<string> Alternatives(IElement select) =>
        select.QuerySelectorAll("fluent-option").Select(o => o.GetAttribute("value")!)
            .Where(v => v != select.GetAttribute("current-value")).ToList();

    private static Task Choose(IRenderedComponent<IComponent> page, int slot, int picker, string value) =>
        SlotSelect(page, slot, picker).QuerySelectorAll("fluent-option")
            .Single(o => o.GetAttribute("value") == value).ClickAsync(new());

    private static string Heading(IElement dialog) => dialog.QuerySelector("h4")!.TextContent.Trim();
}
