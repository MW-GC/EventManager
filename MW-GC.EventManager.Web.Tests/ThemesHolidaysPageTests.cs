using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components.Icons.Regular;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;

namespace MW_GC.EventManager.Web.Tests;

// Issue #125: Themes and Holidays on the shared Library page, rendered as routed pages against a stub API.
// What the move adds (the name rule refuses a bad name before anything is sent) and what each page sets on
// the shared page to keep its own look (the card view, the Edit dialog's Save button). The rest of their
// behaviour is pinned by the tests that covered the old pages and still run unchanged.
[TestClass]
public sealed class ThemesHolidaysPageTests : IDisposable
{
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly ThemeEntity spooky = new() { Name = "Spooky" };
    private readonly HolidayEntity halloween = new() { Name = "Halloween" };

    public ThemesHolidaysPageTests()
    {
        api.Json(HttpMethod.Get, "/api/themes", new[] { spooky })
           .Json(HttpMethod.Get, "/api/holidays", new[] { halloween });
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    // ---- The name rule -------------------------------------------------------------------------

    [TestMethod]
    [DataRow("themes", "blank", "Name is required.")]
    [DataRow("themes", "spaces", "Name is required.")]
    [DataRow("themes", "101", "Name must be 100 characters or fewer.")]
    [DataRow("holidays", "blank", "Name is required.")]
    [DataRow("holidays", "spaces", "Name is required.")]
    [DataRow("holidays", "101", "Name must be 100 characters or fewer.")]
    public async Task Add_RefusesABadName_WithTheApisMessage_AndSendsNothing(string route, string kind, string expected)
    {
        var page = Open(route);
        var singular = Singular(route);
        await page.Button($"Add {singular}").ClickAsync(new());
        await NameField(page).ChangeAsync(new ChangeEventArgs { Value = BadName(kind) });

        await page.FormDialog().QuerySelector("form")!.SubmitAsync();

        Assert.AreEqual($"Add {singular}", page.DialogTitle());
        Assert.AreEqual(expected, Assert.ContainsSingle(page.FormDialog().Alerts()));
        Assert.IsEmpty(Writes());
    }

    [TestMethod]
    [DataRow("themes", "Spooky")]
    [DataRow("holidays", "Halloween")]
    public async Task Edit_RefusesAWhitespaceName_AndKeepsTheItem(string route, string item)
    {
        var page = Open(route);
        await RowButton(page, item, "Edit").ClickAsync(new());
        await NameField(page).ChangeAsync(new ChangeEventArgs { Value = "   " });

        await page.FormDialog().QuerySelector("form")!.SubmitAsync();

        Assert.AreEqual($"Edit {Singular(route)}", page.DialogTitle());
        Assert.AreEqual("Name is required.", Assert.ContainsSingle(page.FormDialog().Alerts()));
        Assert.IsEmpty(Writes());
        Assert.Contains(item, Assert.ContainsSingle(page.Rows()));
    }

    // ---- What each page sets on the shared page ------------------------------------------------

    // SaveText: these Edit dialogs always said "Save", not the shared page's "Save Changes".
    [TestMethod]
    [DataRow("themes", "Spooky")]
    [DataRow("holidays", "Halloween")]
    public async Task EditDialog_SubmitSaysSave(string route, string item)
    {
        var page = Open(route);
        await RowButton(page, item, "Edit").ClickAsync(new());

        var buttons = page.FormDialog().QuerySelectorAll("fluent-button").Select(b => b.TextContent.Trim()).ToList();
        CollectionAssert.AreEqual(new[] { "Cancel", "Save" }, buttons);
    }

    // CardIcon: each card's Edit and Delete act on its own item, with the focus keys the dialogs return to. A
    // cancelled question sends nothing; the confirmed one deletes that card's item and no other.
    [TestMethod]
    [DataRow("themes", "Spooky")]
    [DataRow("holidays", "Halloween")]
    public async Task CardView_EachCardsEditAndDelete_ActOnItsItem(string route, string item)
    {
        var page = Open(route);
        var id = route == "themes" ? spooky.Id : halloween.Id;
        await page.Find("fluent-button[title='Card view']").ClickAsync(new());

        Assert.IsEmpty(page.Rows());
        Assert.AreEqual($"edit-{id}", CardButton(page, item, "Edit").GetAttribute("data-focus-key"));
        Assert.AreEqual($"delete-{id}", CardButton(page, item, "Delete").GetAttribute("data-focus-key"));

        await CardButton(page, item, "Edit").ClickAsync(new());
        Assert.AreEqual($"Edit {Singular(route)}", page.DialogTitle());
        Assert.AreEqual(item, NameField(page).GetAttribute("value"));
        await page.FormDialog().Button("Cancel").ClickAsync(new());
        Assert.IsFalse(page.HasFormDialog());

        await CardButton(page, item, "Delete").ClickAsync(new());
        Assert.AreEqual($"Delete {item}? This cannot be undone.", page.Confirmation().QuerySelector(".confirm-message")!.TextContent.Trim());
        await page.Confirmation().Button("Cancel").ClickAsync(new());
        Assert.IsFalse(page.HasConfirmation());
        Assert.IsEmpty(Writes());

        api.Status(HttpMethod.Delete, $"/api/{route}/{id}", System.Net.HttpStatusCode.NoContent);
        await CardButton(page, item, "Delete").ClickAsync(new());
        await page.ConfirmAsync();
        var delete = Assert.ContainsSingle(Writes());
        Assert.AreEqual((HttpMethod.Delete, $"/api/{route}/{id}"), (delete.Method, delete.Path));
        Assert.IsEmpty(page.PageAlerts());
    }

    // Each page's own look on the shared page (#171): its subtitle under the heading, and its own icon on every card.
    [TestMethod]
    [DataRow("themes", "Manage activity themes for event filtering", "Spooky")]
    [DataRow("holidays", "Manage holiday tags for activity categorization", "Halloween")]
    public async Task Page_ShowsItsSubtitle_AndItsCardIcon(string route, string subtitle, string item)
    {
        var page = Open(route);
        var heading = page.Find("h1.page-title");
        Assert.AreEqual(route == "themes" ? "Themes" : "Holidays", heading.TextContent.Trim());
        Assert.AreEqual(subtitle, heading.NextElementSibling!.TextContent.Trim());

        await page.Find("fluent-button[title='Card view']").ClickAsync(new());
        var card = page.FindAll("fluent-card").Last(c => c.QuerySelector("fluent-button") is not null && c.TextContent.Contains(item));
        // The card's first icon is the page's own: the palette for Themes, the calendar star for Holidays.
        var drawn = card.QuerySelector("svg path")!.GetAttribute("d");
        var own = route == "themes" ? PathOf(new Size24.Color()) : PathOf(new Size24.CalendarStar());
        Assert.AreEqual(own, drawn);
    }

    // The outline an icon draws: the d attribute of its one path.
    private static string PathOf(Microsoft.FluentUI.AspNetCore.Components.Icon icon) =>
        System.Text.RegularExpressions.Regex.Match(icon.Content, "d=\"([^\"]+)\"").Groups[1].Value;

    // ---- Helpers -------------------------------------------------------------------------------

    private IRenderedComponent<IComponent> Open(string route)
    {
        IRenderedComponent<IComponent> page = route == "themes" ? harness.Render<Themes>() : harness.Render<Holidays>();
        page.WaitForRows(1);
        return page;
    }

    private static string Singular(string route) => route == "themes" ? "Theme" : "Holiday";

    private static string BadName(string kind) => kind switch
    {
        "spaces" => "   ",
        "101" => new string('x', 101),
        _ => string.Empty
    };

    private List<(HttpMethod Method, string Path, string? Body)> Writes() =>
        api.Requests.Where(r => r.Method != HttpMethod.Get).ToList();

    private static IElement NameField(IRenderedComponent<IComponent> page) =>
        page.FormDialog().QuerySelector("fluent-text-field")!;

    private static IElement RowButton(IRenderedComponent<IComponent> page, string item, string title) =>
        page.FindAll("tbody tr.fluent-data-grid-row")
            .Where(r => r.Closest("fluent-dialog") is null)
            .Single(r => r.TextContent.Contains(item))
            .QuerySelector($"fluent-button[title={title}]")!;

    private static IElement CardButton(IRenderedComponent<IComponent> page, string item, string title) =>
        page.FindAll("fluent-card")
            .Last(c => c.QuerySelector("fluent-button") is not null && c.TextContent.Contains(item))
            .QuerySelector($"fluent-button[title={title}]")!;
}
