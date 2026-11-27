using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;
using static MW_GC.EventManager.E2ETests.BrowserSession;

namespace MW_GC.EventManager.E2ETests;

/// <summary>
/// The Repeat section of the create Event dialog by keyboard alone (#233.2): Tab reaches the Repeat switch and then,
/// in order, each select the rule shows and the end date, and the switch and the rule select work from the keyboard.
/// A control taken out of the tab order (a disabled select) leaves Tab on the next one, so the flow fails. A
/// <c>tabindex="-1"</c> written on a FluentSelect does not take it out: the web component sets its own tabindex back
/// to 0 in the browser. Nothing is saved, so the flow needs no data.
/// </summary>
/// <remarks>
/// The rules are offered Weekly, Every other week, Monthly on the nth weekday, ..., Yearly on a holiday, so two
/// ArrowDown presses on the closed select move Weekly to Monthly on the nth weekday, and End picks Yearly on a holiday.
/// Focus is checked by role and name: the date box's focus is on the input inside the text field's shadow root, which
/// Playwright's role locators reach.
/// </remarks>
[TestClass]
public sealed class RepeatKeyboardFlows
{
    // The script every FluentSelect imports once it has rendered; its key handler needs it.
    private const string SelectScript = "ListComponentBase.razor.js";

    [TestMethod]
    public Task Tab_ReachesTheRepeatSwitch_ThenEachRuleSelectAndTheEndDate_AndTheKeyboardWorksThem() => RunAsync(async session =>
    {
        var page = session.Page;
        // Started before the page loads, so the one request the selects' script makes cannot be missed.
        var selectScript = page.WaitForResponseAsync(r => r.Url.Contains(SelectScript, StringComparison.Ordinal));
        await session.OpenEventsPageAsync();
        await session.Button("Create Event").ClickAsync();
        await Expect(session.Dialog("Customize Event")).ToBeVisibleAsync(new() { Timeout = UiTimeoutMs });

        // Tab from the Event Time box (the last control above the section) reaches the switch next.
        var repeat = Control(page, AriaRole.Switch, "Repeat");
        var time = page.Locator("fluent-text-field[aria-label='Event Time']");
        await time.Locator("input").FocusAsync();
        await TabOutOfAsync(page, time, repeat);

        // Space turns it on and shows the rule select, which Tab reaches straight after it.
        await page.Keyboard.PressAsync("Space");
        await Expect(repeat).ToHaveAttributeAsync("aria-checked", "true", new() { Timeout = UiTimeoutMs });
        var rule = Control(page, AriaRole.Combobox, "Repeats");
        await Expect(rule).ToBeVisibleAsync(new() { Timeout = UiTimeoutMs });

        // Any key pressed while a FluentSelect holds focus (Tab included) runs its key handler, which throws if the
        // select's script has not loaded yet (its script reference is still null); the page's error boundary then
        // replaces the whole page and the select is gone (#277). The select is visible and focusable well before
        // the script is ready, so the flow waits for the script, and for the page to have taken it, before the
        // first key goes to a select.
        await (await selectScript).FinishedAsync();
        await page.EvaluateAsync("() => new Promise(done => setTimeout(done, 0))");
        await TabAsync(page, rule);
        await TabAsync(page, Control(page, AriaRole.Textbox, "Ends on (optional)"));

        // Monthly on the nth weekday by arrow keys: its two selects come between the rule and the end date.
        await rule.FocusAsync();
        await page.Keyboard.PressAsync("ArrowDown");
        await Expect(rule).ToHaveJSPropertyAsync("value", "BiWeekly", new() { Timeout = UiTimeoutMs });
        await page.Keyboard.PressAsync("ArrowDown");
        await Expect(rule).ToHaveJSPropertyAsync("value", "MonthlyNthWeekday", new() { Timeout = UiTimeoutMs });
        var week = Control(page, AriaRole.Combobox, "Week of the month");
        await Expect(week).ToBeVisibleAsync(new() { Timeout = UiTimeoutMs });
        await Expect(rule).ToBeFocusedAsync(new() { Timeout = UiTimeoutMs });
        await TabAsync(page, week);
        await TabAsync(page, Control(page, AriaRole.Combobox, "Weekday"));
        await TabAsync(page, Control(page, AriaRole.Textbox, "Ends on (optional)"));

        // Yearly on a holiday by Enter, End and Enter: the holiday preset replaces them.
        await rule.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await Expect(rule).ToHaveAttributeAsync("aria-expanded", "true", new() { Timeout = UiTimeoutMs });
        await page.Keyboard.PressAsync("End");
        await page.Keyboard.PressAsync("Enter");
        var preset = Control(page, AriaRole.Combobox, "Holiday preset");
        await Expect(preset).ToBeVisibleAsync(new() { Timeout = UiTimeoutMs });
        await Expect(week).ToHaveCountAsync(0, new() { Timeout = UiTimeoutMs });
        await Expect(rule).ToBeFocusedAsync(new() { Timeout = UiTimeoutMs });
        await TabAsync(page, preset);
        await TabAsync(page, Control(page, AriaRole.Textbox, "Ends on (optional)"));

        // Space on the switch turns it off again and the rule select goes.
        await repeat.FocusAsync();
        await page.Keyboard.PressAsync("Space");
        await Expect(repeat).ToHaveAttributeAsync("aria-checked", "false", new() { Timeout = UiTimeoutMs });
        await Expect(rule).ToHaveCountAsync(0, new() { Timeout = UiTimeoutMs });
    });

    private static ILocator Control(IPage page, AriaRole role, string name) =>
        page.GetByRole(role, new() { Name = name, Exact = true });

    // One Tab must land on the next control.
    private static async Task TabAsync(IPage page, ILocator next)
    {
        await page.Keyboard.PressAsync("Tab");
        await Expect(next).ToBeFocusedAsync(new() { Timeout = UiTimeoutMs });
    }

    // Chromium's time input holds a stop per field (hour, minute, AM/PM), so Tab until focus leaves the time box; the
    // first stop after it must be the target.
    private static async Task TabOutOfAsync(IPage page, ILocator box, ILocator next)
    {
        Assert.IsTrue(await HoldsFocusAsync(box), "The flow could not put focus in the box it tabs out of.");
        for (var i = 0; i < 8 && await HoldsFocusAsync(box); i++) await page.Keyboard.PressAsync("Tab");
        await Expect(next).ToBeFocusedAsync(new() { Timeout = UiTimeoutMs });
    }

    // Focus inside a Fluent text field leaves the host as the document's active element.
    private static Task<bool> HoldsFocusAsync(ILocator host) => host.EvaluateAsync<bool>("e => document.activeElement === e");
}
