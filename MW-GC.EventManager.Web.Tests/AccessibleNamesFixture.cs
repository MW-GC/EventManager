using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// Issue #54 on all five pages, rendered with bUnit against a stub API: the library and Event every page shows, and
// how a test opens a page or one of its dialogs. Shared by AccessibleNamesTests (names, toggles, headings, tags)
// and FocusReturnTests (every dialog returns focus to its opener).
public abstract class AccessibleNamesFixture : IDisposable
{
    internal readonly StubApiHandler api = new();
    internal readonly PageHarness harness;
    protected readonly GameEntity alpha = new() { Name = "Alpha" };
    protected readonly GameEntity beta = new() { Name = "Beta" };
    protected readonly ThemeEntity spooky = new() { Name = "Spooky" };
    protected readonly HolidayEntity halloween = new() { Name = "Halloween" };
    protected readonly ActivityEntity tagged;
    protected readonly ActivityEntity plain;
    protected readonly EventEntity night;

    protected AccessibleNamesFixture()
    {
        tagged = new ActivityEntity { Name = "Tagged", GameId = alpha.Id, ThemeIds = [spooky.Id], HolidayIds = [halloween.Id] };
        plain = new ActivityEntity { Name = "Plain", GameId = beta.Id };
        night = new EventEntity
        {
            Name = "Game night",
            Date = new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero),
            UniqueGamesOnly = false,
            Selections = [Pick(alpha, tagged), Pick(beta, plain)]
        };
        api.Json(HttpMethod.Get, "/api/games", new[] { alpha, beta })
           .Json(HttpMethod.Get, "/api/activities", new[] { tagged, plain })
           .Json(HttpMethod.Get, "/api/themes", new[] { spooky })
           .Json(HttpMethod.Get, "/api/holidays", new[] { halloween })
           .Json(HttpMethod.Get, "/api/events", new[] { night });
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    // Renders a page and waits until every list it loads has arrived (tags and the Events dialog
    // need the Theme and Holiday lists, not only the page's own).
    protected IRenderedComponent<IComponent> Open(string route)
    {
        var reads = api.Requests.Count;
        IRenderedComponent<IComponent> page = route switch
        {
            "games" => harness.Render<Games>(),
            "activities" => harness.Render<Activities>(),
            "themes" => harness.Render<Themes>(),
            "holidays" => harness.Render<Holidays>(),
            _ => harness.Render<Events>()
        };
        var lists = route switch { "events" => 5, "games" or "activities" => 4, _ => 1 };
        page.WaitForAssertion(() => Assert.AreEqual(reads + lists, api.Requests.Count));
        page.WaitForRows(route is "games" or "activities" ? 2 : 1);
        return page;
    }

    protected async Task<IRenderedComponent<IComponent>> OpenDialog(string form)
    {
        var route = form[..form.IndexOf('-')];
        var page = Open(route);
        switch (form)
        {
            case "events-create": await page.Button("Create Event").ClickAsync(new()); break;
            case "games-create": await page.Button("Add Game").ClickAsync(new()); break;
            case "activities-create": await page.Button("Add Activity").ClickAsync(new()); break;
            case "themes-create": await page.Button("Add Theme").ClickAsync(new()); break;
            case "holidays-create": await page.Button("Add Holiday").ClickAsync(new()); break;
            case "events-edit": await RowButton(page, "Game night", "Edit").ClickAsync(new()); break;
            case "events-details": await RowButton(page, "Game night", "Details").ClickAsync(new()); break;
            case "games-edit": await RowButton(page, "Alpha", "Edit").ClickAsync(new()); break;
            case "activities-edit": await RowButton(page, "Tagged", "Edit").ClickAsync(new()); break;
            case "activities-details": await RowButton(page, "Tagged", "Details").ClickAsync(new()); break;
            case "themes-edit": await RowButton(page, "Spooky", "Edit").ClickAsync(new()); break;
            case "holidays-edit": await RowButton(page, "Halloween", "Edit").ClickAsync(new()); break;
            default: throw new ArgumentOutOfRangeException(nameof(form), form);
        }
        Assert.IsTrue(page.HasFormDialog());
        return page;
    }

    protected static IElement Row(IRenderedComponent<IComponent> page, string item) =>
        page.FindAll("tbody tr.fluent-data-grid-row")
            .Where(r => r.Closest("fluent-dialog") is null)
            .Single(r => r.TextContent.Contains(item));
}
