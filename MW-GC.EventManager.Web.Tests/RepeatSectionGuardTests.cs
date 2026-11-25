using System.Globalization;
using System.Net;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// #100, #204: what keeps the Repeat section of the create Event dialog inside a create, on the real Events page over a
// stub API: an Edit saved after a cancelled repeating create is still an Event update, never a Series.
[TestClass]
public sealed class RepeatSectionGuardTests : IDisposable
{
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly GameEntity kart = new() { Name = "Kart" }, brawl = new() { Name = "Brawl" };
    private readonly EventEntity night;
    private readonly CultureInfo culture = CultureInfo.CurrentCulture;

    public RepeatSectionGuardTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var grandPrix = new ActivityEntity { Name = "Grand Prix", GameId = kart.Id };
        var stock = new ActivityEntity { Name = "Stock", GameId = brawl.Id };
        night = new EventEntity { Name = "Game night", Date = new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero), Selections = [Pick(kart, grandPrix)] };
        api.Json(HttpMethod.Get, "/api/games", new[] { kart, brawl })
           .Json(HttpMethod.Get, "/api/activities", new[] { grandPrix, stock })
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>())
           .Json(HttpMethod.Get, "/api/events", new[] { night })
           .Status(HttpMethod.Post, "/api/series", HttpStatusCode.Created, "{}")
           .Status(HttpMethod.Post, "/api/events", HttpStatusCode.Created, "{}")
           .Status(HttpMethod.Put, $"/api/events/{night.Id}", HttpStatusCode.OK, "{}");
        harness = new PageHarness(api);
    }

    public void Dispose()
    {
        harness.Dispose();
        CultureInfo.CurrentCulture = culture;
    }

    // Repeat turned on in a create that was cancelled must not leak into the next Edit: the Edit never shows Repeat,
    // and its save is the Event's own PUT, with no Series sent.
    [TestMethod]
    public async Task AnEditSaved_AfterARepeatingCreateWasCancelled_UpdatesTheEvent_AndSendsNoSeries()
    {
        var page = OpenPage();
        await page.Button("Create Event").ClickAsync(new());
        await SwitchRepeat(page, true);
        Assert.IsTrue(RepeatSwitch(page).HasAttribute("current-checked"), "Repeat did not turn on in the create");
        await page.FormDialog().Button("Cancel").ClickAsync(new());

        await RowButton(page, "Game night", "Edit").ClickAsync(new());
        Assert.AreEqual("Edit Event", page.DialogTitle());
        Assert.IsNull(page.FormDialog().QuerySelector(".repeat-section"));
        await page.FormDialog().Button("Update Event").ClickAsync(new());

        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog(), string.Join(" | ", page.HasFormDialog() ? page.FormDialog().Alerts() : [])));
        Assert.AreEqual(0, api.Count(HttpMethod.Post, "/api/series"), "an Edit sent a Series");
        Assert.AreEqual(0, api.Count(HttpMethod.Post, "/api/events"), "an Edit created an Event");
        var sent = JsonSerializer.Deserialize<EventEntity>(Assert.ContainsSingle(api.Bodies(HttpMethod.Put, $"/api/events/{night.Id}"))!, JsonSerializerOptions.Web)!;
        Assert.AreEqual(night.Id, sent.Id);
        Assert.AreEqual("Game night", sent.Name);
    }

    // Turning Repeat on is an unsaved change like any other input: Escape asks "Discard changes?". Turning it off again
    // makes the dialog clean, so Escape closes it at once. Nothing is sent either way.
    [TestMethod]
    public async Task TurningRepeatOn_AsksToDiscard_AndTurningItOffAgainClosesAtOnce()
    {
        var page = OpenPage();
        await page.Button("Create Event").ClickAsync(new());
        await SwitchRepeat(page, true);

        await Dismiss(page);
        Assert.IsTrue(page.HasFormDialog(), "Escape closed the dialog and lost Repeat");
        Assert.IsTrue(page.HasConfirmation(), "turning Repeat on did not count as a change");
        Assert.AreEqual("Discard changes?", page.Confirmation().QuerySelector("h4")!.TextContent.Trim());
        await page.Confirmation().Button("Keep editing").ClickAsync(new());
        Assert.IsTrue(RepeatSwitch(page).HasAttribute("current-checked"), "Keep editing lost Repeat");

        await SwitchRepeat(page, false);
        await Dismiss(page);
        Assert.IsFalse(page.HasConfirmation(), "Repeat turned off again still asked to discard");
        Assert.IsFalse(page.HasFormDialog());
        Assert.IsEmpty(api.Requests.Where(r => r.Method != HttpMethod.Get).ToList());
    }

    // A holiday preset is a date rule, not an Activity's Holiday tag (GLOSSARY.md, ADR 0002), so its select is named
    // "Holiday preset". On Thanksgiving 2026 it starts on Thanksgiving, and the preset the admin picks is the selected one.
    [TestMethod]
    public async Task TheHolidayPresetSelect_IsNamedHolidayPreset_AndShowsTheSelectedPreset()
    {
        var page = OpenPage();
        await page.Button("Create Event").ClickAsync(new());
        await page.FormDialog().QuerySelector("fluent-text-field.fluent-datepicker")!.ChangeAsync(new ChangeEventArgs { Value = "11/26/2026" });
        await SwitchRepeat(page, true);
        await Choose(page, "Repeats", "AnnualHoliday");

        var preset = Select(page, "Holiday preset");
        Assert.AreEqual("Holiday preset", preset.GetAttribute("aria-label"));
        Assert.AreEqual("Thanksgiving", preset.GetAttribute("current-value"));
        Assert.AreEqual("Thanksgiving", preset.QuerySelector("fluent-option[selected]")!.TextContent.Trim());
        Assert.IsFalse(page.FormDialog().QuerySelectorAll("fluent-select").Any(s => s.GetAttribute("aria-label") == "Holiday"),
            "a select is still named Holiday, the Activity tag's name");

        await Choose(page, "Holiday preset", "Christmas");
        Assert.AreEqual("Christmas", Select(page, "Holiday preset").GetAttribute("current-value"));
        Assert.AreEqual("Christmas Day", Select(page, "Holiday preset").QuerySelector("fluent-option[selected]")!.TextContent.Trim());
    }

    // ---- helpers -------------------------------------------------------------------------------

    private IRenderedComponent<Events> OpenPage()
    {
        var page = harness.Render<Events>();
        page.WaitForRows(1);
        return page;
    }

    private static IElement RepeatSwitch(IRenderedComponent<Events> page) =>
        page.FormDialog().QuerySelectorAll("fluent-switch").Single(s => s.GetAttribute("aria-label") == "Repeat");

    private static Task SwitchRepeat(IRenderedComponent<Events> page, bool on) =>
        RepeatSwitch(page).TriggerEventAsync("onswitchcheckedchange", new CheckboxChangeEventArgs { Checked = on });

    private static IElement Select(IRenderedComponent<Events> page, string label) =>
        page.FormDialog().QuerySelector(".repeat-section")!.QuerySelectorAll("fluent-select").Single(s => s.GetAttribute("aria-label") == label);

    private static Task Choose(IRenderedComponent<Events> page, string label, string value) =>
        Select(page, label).QuerySelectorAll("fluent-option").Single(o => o.GetAttribute("value") == value).ClickAsync(new());
}
