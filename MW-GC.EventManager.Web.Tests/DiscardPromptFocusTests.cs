using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Pages;

namespace MW_GC.EventManager.Web.Tests;

// Issues #109 and #110: the "Discard changes?" question over a form dialog. Fluent UI 4.14.4's dialog
// reads trap-focus only when it connects, so the form dialog's trap-focus="false" never lets go of
// focus; the two dialogs then pull focus back and forth (RangeError on Tab), focus stays in the form
// behind the question (Enter submits it), and Keep editing leaves focus on <body>.
// ConfirmDialog therefore tells focus.js when it opens (emFocus.question: move focus into the
// question) and when it closes (emFocus.answered: put focus back in a form dialog that is still open).
// What focus.js then does with the DOM needs a real browser; these tests prove the calls are made at
// the right moments, and that a delete question keeps its own focus return.
[TestClass]
public sealed class DiscardPromptFocusTests : IDisposable
{
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly GameEntity kart = new() { Name = "Kart" };
    private readonly ActivityEntity grandPrix;
    private readonly Activity retired;
    private readonly ThemeEntity spooky = new() { Name = "Spooky" };
    private readonly HolidayEntity halloween = new() { Name = "Halloween" };
    private readonly EventEntity night;
    private readonly EventEntity party;

    public DiscardPromptFocusTests()
    {
        grandPrix = new ActivityEntity { Name = "Grand Prix", GameId = kart.Id };
        retired = new Activity { Id = Guid.NewGuid(), GameId = kart.Id, Name = "Retired", Comments = "Old note" };
        night = new EventEntity
        {
            Name = "Game night",
            Date = new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero),
            UniqueGamesOnly = false,
            Selections = [new Selection { Game = new Game { Id = kart.Id, Name = kart.Name }, Activity = new Activity { Id = grandPrix.Id, GameId = kart.Id, Name = grandPrix.Name } }]
        };
        // Has a winner, so its Details dialog shows the comment box.
        party = new EventEntity
        {
            Name = "Party",
            Date = new DateTimeOffset(2026, 10, 31, 20, 0, 0, TimeSpan.Zero),
            Selections = [new Selection { Game = new Game { Id = kart.Id, Name = kart.Name }, Activity = retired }],
            WinnerActivityId = retired.Id
        };
        api.Json(HttpMethod.Get, "/api/games", new[] { kart })
           .Json(HttpMethod.Get, "/api/activities", new[] { grandPrix })
           .Json(HttpMethod.Get, "/api/themes", new[] { spooky })
           .Json(HttpMethod.Get, "/api/holidays", new[] { halloween })
           .Json(HttpMethod.Get, "/api/events", new[] { night, party });
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    [TestMethod]
    [DataRow("games-create")]
    [DataRow("games-edit")]
    [DataRow("activities-create")]
    [DataRow("activities-edit")]
    [DataRow("activities-details")]
    [DataRow("themes-create")]
    [DataRow("themes-edit")]
    [DataRow("holidays-create")]
    [DataRow("holidays-edit")]
    [DataRow("events-create")]
    [DataRow("events-edit")]
    [DataRow("events-details")]
    public async Task DiscardQuestion_TakesFocus_KeepEditingGivesItBackToTheForm(string form)
    {
        var page = await OpenChangedForm(form);
        harness.JSInterop.VerifyNotInvoke("emFocus.question");

        // Escape on the form: the question opens and asks focus.js to move focus into it.
        await Dismiss(page);
        Assert.IsTrue(page.HasConfirmation());
        Assert.AreEqual("false", page.FormDialog().GetAttribute("trap-focus"));
        harness.JSInterop.VerifyInvoke("emFocus.question", 1);
        harness.JSInterop.VerifyNotInvoke("emFocus.answered");

        // Keep editing by click: the question is gone, the form stays, and focus goes back into it,
        // not to the button that opened the form.
        await page.Confirmation().Button("Keep editing").ClickAsync(new());
        Assert.IsFalse(page.HasConfirmation());
        Assert.IsTrue(page.HasFormDialog());
        Assert.AreEqual("true", page.FormDialog().GetAttribute("trap-focus"));
        harness.JSInterop.VerifyInvoke("emFocus.answered", 1);
        harness.JSInterop.VerifyNotInvoke("emFocus.restore");

        // Escape while the question is open answers it with Keep editing: the same focus return.
        await Dismiss(page);
        harness.JSInterop.VerifyInvoke("emFocus.question", 2);
        await Dismiss(page);
        Assert.IsFalse(page.HasConfirmation());
        Assert.IsTrue(page.HasFormDialog());
        harness.JSInterop.VerifyInvoke("emFocus.answered", 2);
        harness.JSInterop.VerifyNotInvoke("emFocus.restore");

        // Discard closes both; the form dialog returns focus to its opener as before.
        await Dismiss(page);
        await page.ConfirmAsync("Discard");
        Assert.IsFalse(page.HasFormDialog());
        Assert.IsFalse(page.HasConfirmation());
        harness.JSInterop.VerifyInvoke("emFocus.question", 3);
        harness.JSInterop.VerifyInvoke("emFocus.answered", 3);
        harness.JSInterop.VerifyInvoke("emFocus.restore", 1);
        Assert.IsEmpty(api.Requests.Where(r => r.Method != HttpMethod.Get).ToList());
    }

    // A delete question keeps its behaviour: Cancel returns focus to the Delete button through the
    // page's FocusReturn. focus.js sees no form dialog behind it and leaves focus alone.
    [TestMethod]
    [DataRow("games", "Kart")]
    [DataRow("themes", "Spooky")]
    [DataRow("events", "Game night")]
    public async Task DeleteQuestion_CancelStillRestoresFocusThroughThePage(string route, string item)
    {
        var page = Open(route);
        await RowButton(page, item, "Delete").ClickAsync(new());
        Assert.IsTrue(page.HasConfirmation());
        Assert.IsFalse(page.HasFormDialog());

        await page.Confirmation().Button("Cancel").ClickAsync(new());

        Assert.IsFalse(page.HasConfirmation());
        harness.JSInterop.VerifyInvoke("emFocus.restore", 1);
        Assert.IsEmpty(api.Requests.Where(r => r.Method != HttpMethod.Get).ToList());
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private IRenderedComponent<IComponent> Open(string route)
    {
        IRenderedComponent<IComponent> page = route switch
        {
            "games" => harness.Render<Games>(),
            "activities" => harness.Render<Activities>(),
            "themes" => harness.Render<Themes>(),
            "holidays" => harness.Render<Holidays>(),
            _ => harness.Render<Events>()
        };
        page.WaitForRows(route == "events" ? 2 : 1);
        return page;
    }

    // Opens one form or details dialog and types into its first text box, so Escape asks first.
    private async Task<IRenderedComponent<IComponent>> OpenChangedForm(string form)
    {
        var route = form[..form.IndexOf('-')];
        var page = Open(route);
        switch (form)
        {
            case "games-create": await page.Button("Add Game").ClickAsync(new()); break;
            case "activities-create": await page.Button("Add Activity").ClickAsync(new()); break;
            case "themes-create": await page.Button("Add Theme").ClickAsync(new()); break;
            case "holidays-create": await page.Button("Add Holiday").ClickAsync(new()); break;
            case "events-create": await page.Button("Create Event").ClickAsync(new()); break;
            case "games-edit": await RowButton(page, "Kart", "Edit").ClickAsync(new()); break;
            case "activities-edit": await RowButton(page, "Grand Prix", "Edit").ClickAsync(new()); break;
            case "activities-details": await RowButton(page, "Grand Prix", "Details").ClickAsync(new()); break;
            case "themes-edit": await RowButton(page, "Spooky", "Edit").ClickAsync(new()); break;
            case "holidays-edit": await RowButton(page, "Halloween", "Edit").ClickAsync(new()); break;
            case "events-edit": await RowButton(page, "Game night", "Edit").ClickAsync(new()); break;
            case "events-details": await RowButton(page, "Party", "Details").ClickAsync(new()); break;
            default: throw new ArgumentOutOfRangeException(nameof(form), form);
        }
        Assert.IsTrue(page.HasFormDialog());
        var field = form.EndsWith("-details")
            ? page.FormDialog().QuerySelector("fluent-text-area")!
            : form.StartsWith("events")
                ? page.FormDialog().QuerySelector("fluent-text-field[placeholder='Enter event name...']")!
                : page.FormDialog().QuerySelector("fluent-text-field")!;
        await field.InputAsync(new ChangeEventArgs { Value = "Typed but not saved" });
        return page;
    }

    // Overlay click or Escape on the page's form or details dialog.
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
}
