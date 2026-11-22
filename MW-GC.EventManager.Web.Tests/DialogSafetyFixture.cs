using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Pages;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// Issue #52 on all five pages, rendered with bUnit against a stub API (DialogSafetyTests, DismissSafetyTests).
// Overlay click and Escape reach a page as the "ondialogdismiss" event that Fluent UI raises on <fluent-dialog>
// (the browser "dismiss" event), so PageActions.Dismiss triggers exactly that event.
public abstract class DialogSafetyFixture : IDisposable
{
    internal readonly StubApiHandler api = new();
    internal readonly PageHarness harness;
    protected readonly GameEntity kart = new() { Name = "Kart" };
    protected readonly GameEntity brawl = new() { Name = "Brawl" };
    protected readonly GameEntity puzzle = new() { Name = "Puzzle" };
    protected readonly ActivityEntity grandPrix;
    protected readonly ActivityEntity timeTrial;
    protected readonly ActivityEntity stock;
    // Used by the "Party" Event but no longer in the library (Party's Winner is Grand Prix, which is).
    protected readonly Activity retired;
    protected readonly ThemeEntity spooky = new() { Name = "Spooky" };
    protected readonly HolidayEntity halloween = new() { Name = "Halloween" };
    protected readonly EventEntity night;
    protected readonly EventEntity party;

    protected DialogSafetyFixture()
    {
        grandPrix = new ActivityEntity { Name = "Grand Prix", GameId = kart.Id };
        timeTrial = new ActivityEntity { Name = "Time Trial", GameId = kart.Id };
        stock = new ActivityEntity { Name = "Stock", GameId = brawl.Id };
        retired = new Activity { Id = Guid.NewGuid(), GameId = brawl.Id, Name = "Retired" };
        night = new EventEntity
        {
            Name = "Game night",
            Date = new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero),
            UniqueGamesOnly = false,
            Selections = [Pick(kart, grandPrix), Pick(brawl, stock), Pick(kart, timeTrial)]
        };
        // One Selection no longer in the library, and a Winner that is (its details show the Comments panel).
        party = new EventEntity
        {
            Name = "Party",
            Date = new DateTimeOffset(2026, 10, 31, 20, 0, 0, TimeSpan.Zero),
            Selections = [new Selection { Game = new Game { Id = brawl.Id, Name = brawl.Name }, Activity = retired }, Pick(kart, grandPrix)],
            WinnerActivityId = grandPrix.Id
        };
        api.Json(HttpMethod.Get, "/api/games", new[] { kart, brawl, puzzle })
           .Json(HttpMethod.Get, "/api/activities", new[] { grandPrix, timeTrial, stock })
           .Json(HttpMethod.Get, "/api/themes", new[] { spooky })
           .Json(HttpMethod.Get, "/api/holidays", new[] { halloween })
           .Json(HttpMethod.Get, "/api/events", new[] { night, party })
           // Grand Prix's Comments, in its Activity details (#131) and as the Party Event's Winner in Event details
           // (#132): none yet, so the first text box is New Comment. The retired Activity's are not routed, so they
           // answer 404, as the real API does for an Activity no longer in the library.
           .Json(HttpMethod.Get, $"/api/activities/{grandPrix.Id}/comments", Array.Empty<CommentEntity>());
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    internal IRenderedComponent<IComponent> Open(string route)
    {
        IRenderedComponent<IComponent> page = route switch
        {
            "games" => harness.Render<Games>(),
            "activities" => harness.Render<Activities>(),
            "themes" => harness.Render<Themes>(),
            "holidays" => harness.Render<Holidays>(),
            _ => harness.Render<Events>()
        };
        page.WaitForRows(route switch { "games" or "activities" => 3, "events" => 2, _ => 1 });
        return page;
    }

    // Opens one form dialog, optionally changes one bound value, and returns how to read that value back.
    internal async Task<(IRenderedComponent<IComponent> Page, Func<string?> Value)> OpenForm(string form, bool change)
    {
        var route = form[..form.IndexOf('-')];
        var page = Open(route);
        Func<string?> value = () => TextField(page).GetAttribute("value");
        IElement? field = null;
        switch (form)
        {
            case "games-create":
                await page.Button("Add Game").ClickAsync(new());
                break;
            case "games-edit":
                await RowButton(page, "Kart", "Edit").ClickAsync(new());
                break;
            case "themes-create":
                await page.Button("Add Theme").ClickAsync(new());
                break;
            case "holidays-create":
                await page.Button("Add Holiday").ClickAsync(new());
                break;
            case "themes-edit":
            case "holidays-edit":
                await page.Find("fluent-button[title=Edit]").ClickAsync(new());
                break;
            case "activities-create":
                await page.Button("Add Activity").ClickAsync(new());
                break;
            case "activities-edit":
                await RowButton(page, "Grand Prix", "Edit").ClickAsync(new());
                break;
            case "activities-details":
                await RowButton(page, "Grand Prix", "Details").ClickAsync(new());
                value = () => page.FormDialog().QuerySelector("fluent-text-area")!.GetAttribute("value");
                if (change) field = page.FormDialog().QuerySelector("fluent-text-area");
                break;
            case "events-create":
                await page.Button("Create Event").ClickAsync(new());
                value = () => EventName(page).GetAttribute("value");
                if (change) field = EventName(page);
                break;
            case "events-edit":
            case "events-edit-slots":
                await RowButton(page, "Game night", "Edit").ClickAsync(new());
                value = () => EventName(page).GetAttribute("value");
                if (change) field = EventName(page);
                break;
            case "events-edit-deleted-activity":
                await RowButton(page, "Party", "Edit").ClickAsync(new());
                Assert.Contains("(deleted) Retired", page.FormDialog().TextContent);
                break;
            case "events-details":
                await RowButton(page, "Party", "Details").ClickAsync(new());
                value = () => page.FormDialog().QuerySelector("fluent-text-area")!.GetAttribute("value");
                if (change) field = page.FormDialog().QuerySelector("fluent-text-area");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(form), form);
        }
        Assert.IsTrue(page.HasFormDialog());
        if (!change) return (page, value);

        if (form == "events-edit-slots")
        {
            value = () => page.FormDialog().QuerySelectorAll("fluent-button[aria-label^='Re-roll slot']").Length.ToString();
            await page.FormDialog().QuerySelectorAll("fluent-button[title='Remove slot']")[0].ClickAsync(new());
            Assert.AreEqual("2", value());
        }
        else
        {
            await (field ?? TextField(page)).ChangeAsync(new ChangeEventArgs { Value = "Typed but not saved" });
            Assert.AreEqual("Typed but not saved", value());
        }
        return (page, value);
    }

    protected static IElement TextField(IRenderedComponent<IComponent> page) =>
        page.FormDialog().QuerySelector("fluent-text-field")!;

    // The dialog's free-text fields in page order. The date and time pickers also render as
    // fluent-text-field, but they are not typed into as free text, so they are left out.
    protected static List<IElement> TextInputs(IRenderedComponent<IComponent> page) =>
        page.FormDialog().QuerySelectorAll("fluent-text-field:not(.fluent-datepicker):not(.fluent-timepicker), fluent-text-area").ToList();

    protected static IElement EventName(IRenderedComponent<IComponent> page) =>
        page.FormDialog().QuerySelector("fluent-text-field[placeholder='Enter event name...']")!;

    protected static string Heading(IElement dialog) => dialog.QuerySelector("h4")!.TextContent.Trim();

    protected static string Message(IElement dialog) => dialog.QuerySelector(".confirm-message")!.TextContent.Trim();
}
