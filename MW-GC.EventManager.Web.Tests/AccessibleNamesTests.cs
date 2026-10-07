using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Pages;
using MW_GC.EventManager.Web.Services;
using MW_GC.EventManager.Web.Shared;

namespace MW_GC.EventManager.Web.Tests;

// Issue #54 on all five pages, rendered with bUnit against a stub API: icon-only buttons, searches,
// pickers and dialogs carry accessible names; view toggles say which is on; Theme and Holiday tags
// differ by more than colour; the page title is a focusable h1 with a matching tab title; and every
// dialog returns focus to its opener (through FocusReturn's emFocus.remember / emFocus.restore).
[TestClass]
public sealed class AccessibleNamesTests : IDisposable
{
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly GameEntity alpha = new() { Name = "Alpha" };
    private readonly GameEntity beta = new() { Name = "Beta" };
    private readonly ThemeEntity spooky = new() { Name = "Spooky" };
    private readonly HolidayEntity halloween = new() { Name = "Halloween" };
    private readonly ActivityEntity tagged;
    private readonly ActivityEntity plain;
    private readonly EventEntity night;

    public AccessibleNamesTests()
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

    private static Selection Pick(GameEntity game, ActivityEntity act) => new()
    {
        Game = new Game { Id = game.Id, Name = game.Name },
        Activity = new Activity { Id = act.Id, GameId = act.GameId, Name = act.Name }
    };

    // ---- Icon-only buttons -------------------------------------------------------------------

    [TestMethod]
    [DataRow("events", "Game night", "Details,Edit,Delete")]
    [DataRow("games", "Alpha", "Edit,Delete")]
    [DataRow("activities", "Tagged", "Details,Edit,Duplicate,Delete")]
    [DataRow("themes", "Spooky", "Edit,Delete")]
    [DataRow("holidays", "Halloween", "Edit,Delete")]
    public void TableRowButtons_AreNamedWithTheirItem(string route, string item, string actions)
    {
        var page = Open(route);
        var row = Row(page, item);

        foreach (var action in actions.Split(','))
        {
            var button = row.QuerySelector($"fluent-button[title={action}]");
            Assert.IsNotNull(button, action);
            Assert.AreEqual($"{action} {item}", button.GetAttribute("aria-label"));
        }
    }

    [TestMethod]
    [DataRow("themes", "Spooky")]
    [DataRow("holidays", "Halloween")]
    public async Task CardViewButtons_AreNamedWithTheirItem(string route, string item)
    {
        var page = Open(route);
        await page.Find("fluent-button[title='Card view']").ClickAsync(new());

        var card = page.FindAll("fluent-card").Last(c => c.QuerySelector("fluent-button") is not null && c.TextContent.Contains(item));
        var labels = card.QuerySelectorAll("fluent-button").Select(b => b.GetAttribute("aria-label")).ToList();
        CollectionAssert.AreEqual(new[] { $"Edit {item}", $"Delete {item}" }, labels);
    }

    [TestMethod]
    public async Task GamesCardViewButtons_ShowTheirText()
    {
        var page = Open("games");
        await page.Find("fluent-button[title='Card view']").ClickAsync(new());

        var card = page.FindAll("fluent-card").Last(c => c.QuerySelector("fluent-button") is not null && c.TextContent.Contains("Alpha"));
        var texts = card.QuerySelectorAll("fluent-button").Select(b => b.TextContent.Trim()).ToList();
        CollectionAssert.AreEqual(new[] { "Edit", "Delete" }, texts);
    }

    [TestMethod]
    [DataRow("games")]
    [DataRow("themes")]
    [DataRow("holidays")]
    public async Task ViewToggles_ExposeAriaPressed(string route)
    {
        var page = Open(route);
        string? Pressed(string title) => page.Find($"fluent-button[title='{title}']").GetAttribute("aria-pressed");
        Assert.AreEqual("true", Pressed("Table view"));
        Assert.AreEqual("false", Pressed("Card view"));

        await page.Find("fluent-button[title='Card view']").ClickAsync(new());
        Assert.AreEqual("false", Pressed("Table view"));
        Assert.AreEqual("true", Pressed("Card view"));

        await page.Find("fluent-button[title='Table view']").ClickAsync(new());
        Assert.AreEqual("true", Pressed("Table view"));
        Assert.AreEqual("false", Pressed("Card view"));
    }

    // ---- Searches and pickers ------------------------------------------------------------------

    [TestMethod]
    [DataRow("events", "Search events")]
    [DataRow("games", "Search games")]
    [DataRow("activities", "Search activities")]
    [DataRow("themes", "Search themes")]
    [DataRow("holidays", "Search holidays")]
    public void Search_HasAccessibleName(string route, string name)
    {
        var page = Open(route);
        Assert.AreEqual(name, page.Find("fluent-search").GetAttribute("aria-label"));
    }

    [TestMethod]
    public void ActivitiesGameFilter_HasAccessibleName()
    {
        var page = Open("activities");
        var filter = page.FindAll("fluent-select").Single(s => s.Closest("fluent-dialog") is null);
        Assert.AreEqual("Filter by game", filter.GetAttribute("aria-label"));
    }

    [TestMethod]
    public async Task EventDialog_SlotPickersTimeAndRemoveButtons_AreNamed()
    {
        var page = Open("events");
        await Row(page, "Game night").QuerySelector("fluent-button[title=Edit]")!.ClickAsync(new());
        var dialog = page.FormDialog();

        var pickers = dialog.QuerySelectorAll("fluent-select").Select(s => s.GetAttribute("aria-label")).ToList();
        CollectionAssert.AreEqual(new[] { "Game for slot 1", "Activity for slot 1", "Game for slot 2", "Activity for slot 2" }, pickers);
        var removes = dialog.QuerySelectorAll("fluent-button[title='Remove slot']").Select(b => b.GetAttribute("aria-label")).ToList();
        CollectionAssert.AreEqual(new[] { "Remove slot 1", "Remove slot 2" }, removes);
        Assert.AreEqual("Event Time", dialog.QuerySelector(".fluent-timepicker")!.GetAttribute("aria-label"));
    }

    [TestMethod]
    public async Task EventDialog_FilterCheckboxLists_AreLabelledGroups()
    {
        var page = Open("events");
        await page.Button("Create Event").ClickAsync(new());

        var groups = Groups(page.FormDialog());
        CollectionAssert.AreEqual(new[] { "Filter by games", "Filter by themes", "Filter by holidays" }, groups.Keys.ToList());
        CollectionAssert.AreEqual(new[] { "Alpha", "Beta" }, groups["Filter by games"]);
        CollectionAssert.AreEqual(new[] { "Spooky" }, groups["Filter by themes"]);
        CollectionAssert.AreEqual(new[] { "Halloween" }, groups["Filter by holidays"]);
    }

    [TestMethod]
    public async Task ActivityForm_ThemeAndHolidayCheckboxLists_AreLabelledGroups()
    {
        var page = Open("activities");
        await page.Button("Add Activity").ClickAsync(new());

        var groups = Groups(page.FormDialog());
        CollectionAssert.AreEqual(new[] { "Themes", "Holidays" }, groups.Keys.ToList());
        CollectionAssert.AreEqual(new[] { "Spooky" }, groups["Themes"]);
        CollectionAssert.AreEqual(new[] { "Halloween" }, groups["Holidays"]);
    }

    // ---- Dialog names --------------------------------------------------------------------------

    [TestMethod]
    [DataRow("events-create", "Customize Event")]
    [DataRow("events-edit", "Edit Event")]
    [DataRow("events-details", "Game night")]
    [DataRow("games-create", "Add Game")]
    [DataRow("games-edit", "Edit Game")]
    [DataRow("activities-create", "Add Activity")]
    [DataRow("activities-edit", "Edit Activity")]
    [DataRow("activities-details", "Tagged")]
    [DataRow("themes-create", "Add Theme")]
    [DataRow("themes-edit", "Edit Theme")]
    [DataRow("holidays-create", "Add Holiday")]
    [DataRow("holidays-edit", "Edit Holiday")]
    public async Task EveryDialog_IsNamedByItsHeading(string form, string heading)
    {
        var page = await OpenDialog(form);

        var dialog = page.FormDialog();
        Assert.AreEqual(heading, dialog.QuerySelector("h3, h4")!.TextContent.Trim());
        Assert.AreEqual(heading, dialog.GetAttribute("aria-label"));
    }

    // Dialog titles keep their h3/h4 look but sit one level under the page h1; sub-headings one more.
    [TestMethod]
    [DataRow("events-create", "h4", "")]
    [DataRow("events-details", "h3", "h6")]
    [DataRow("games-edit", "h4", "h6")]
    [DataRow("activities-details", "h3", "h6")]
    [DataRow("themes-create", "h4", "")]
    [DataRow("holidays-edit", "h4", "")]
    public async Task DialogHeadings_FollowThePageHeadingLevel(string form, string title, string sub)
    {
        var page = await OpenDialog(form);
        var dialog = page.FormDialog();

        Assert.AreEqual("2", dialog.QuerySelector(title)!.GetAttribute("aria-level"));
        if (sub.Length > 0)
        {
            var subs = dialog.QuerySelectorAll(sub);
            Assert.IsTrue(subs.Length > 0, sub);
            Assert.IsTrue(subs.All(h => h.GetAttribute("aria-level") == "3"), sub);
        }
    }

    [TestMethod]
    public async Task DeleteConfirmation_HeadingFollowsThePageHeadingLevel()
    {
        var page = Open("themes");
        await Row(page, "Spooky").QuerySelector("fluent-button[title=Delete]")!.ClickAsync(new());

        Assert.AreEqual("2", page.Confirmation().QuerySelector("h4")!.GetAttribute("aria-level"));
    }

    [TestMethod]
    public async Task ActivityDetails_CloseIconButton_IsNamed()
    {
        var page = await OpenDialog("activities-details");
        var close = page.FormDialog().QuerySelectorAll("fluent-button[title=Close]").Single();
        Assert.AreEqual("Close", close.GetAttribute("aria-label"));
    }

    // ---- Theme and Holiday tags ----------------------------------------------------------------

    [TestMethod]
    public void TagBadge_DiffersByIconAndHiddenPrefix_NotOnlyColour()
    {
        var theme = harness.Render<TagBadge>(p => p.Add(t => t.Kind, TagBadge.TagKind.Theme).Add(t => t.Text, "Spooky"));
        var holiday = harness.Render<TagBadge>(p => p.Add(t => t.Kind, TagBadge.TagKind.Holiday).Add(t => t.Text, "Halloween"));

        Assert.AreEqual("Theme: Spooky", Read(theme.Find("fluent-badge")));
        Assert.AreEqual("Holiday: Halloween", Read(holiday.Find("fluent-badge")));
        Assert.AreEqual("Theme: ", theme.Find(".visually-hidden").TextContent);
        Assert.AreEqual("Holiday: ", holiday.Find(".visually-hidden").TextContent);
        // Each kind has its own icon, hidden from assistive tech (the prefix already says it).
        var themeIcon = theme.Find("fluent-badge svg");
        var holidayIcon = holiday.Find("fluent-badge svg");
        Assert.AreEqual("true", themeIcon.GetAttribute("aria-hidden"));
        Assert.AreEqual("true", holidayIcon.GetAttribute("aria-hidden"));
        Assert.AreNotEqual(themeIcon.InnerHtml, holidayIcon.InnerHtml);
        // The colours stay as they were: Theme neutral, Holiday accent.
        Assert.AreEqual("neutral", theme.Find("fluent-badge").GetAttribute("appearance"));
        Assert.AreEqual("accent", holiday.Find("fluent-badge").GetAttribute("appearance"));
    }

    [TestMethod]
    public async Task Tags_OnEveryPage_UseTagBadge()
    {
        var activities = Open("activities");
        CollectionAssert.AreEqual(new[] { "Theme: Spooky", "Holiday: Halloween" }, Tags(Row(activities, "Tagged")));
        await Row(activities, "Tagged").QuerySelector("fluent-button[title=Details]")!.ClickAsync(new());
        CollectionAssert.AreEqual(new[] { "Theme: Spooky", "Holiday: Halloween" }, Tags(activities.FormDialog()));

        var games = Open("games");
        await Row(games, "Alpha").QuerySelector("fluent-button[title=Edit]")!.ClickAsync(new());
        CollectionAssert.AreEqual(new[] { "Theme: Spooky", "Holiday: Halloween" }, Tags(games.FormDialog()));

        var events = Open("events");
        await Row(events, "Game night").QuerySelector("fluent-button[title=Edit]")!.ClickAsync(new());
        CollectionAssert.AreEqual(new[] { "Theme: Spooky", "Holiday: Halloween" }, Tags(events.FormDialog()));
    }

    // ---- Page heading and tab title ------------------------------------------------------------

    [TestMethod]
    [DataRow("events", "Events")]
    [DataRow("games", "Games")]
    [DataRow("activities", "Activities")]
    [DataRow("themes", "Themes")]
    [DataRow("holidays", "Holidays")]
    public void EveryPage_HasOneFocusableH1(string route, string title)
    {
        var page = Open(route);
        var h1 = page.FindAll("h1").Single();
        Assert.AreEqual(title, h1.TextContent.Trim());
        Assert.AreEqual("-1", h1.GetAttribute("tabindex"));
        Assert.AreEqual("page-title", h1.ClassName);
    }

    [TestMethod]
    public void NotFound_HasH1()
    {
        var page = harness.Render<NotFound>();
        var h1 = page.FindAll("h1").Single();
        Assert.AreEqual("Not Found", h1.TextContent.Trim());
        Assert.AreEqual("-1", h1.GetAttribute("tabindex"));
    }

    [TestMethod]
    public void PageHeader_SetsTabTitle()
    {
        var head = harness.Render<HeadOutlet>(_ => { });
        harness.Render<PageHeader>(p => p.Add(h => h.Title, "Themes"));

        head.WaitForAssertion(() => Assert.AreEqual("Themes – Event Manager", head.Find("title").TextContent));
    }

    // ---- Focus return --------------------------------------------------------------------------

    [TestMethod]
    [DataRow("events-create", "add", "Cancel")]
    [DataRow("events-edit", "edit", "Cancel")]
    [DataRow("events-details", "details", "Close")]
    [DataRow("games-create", "add", "Cancel")]
    [DataRow("games-edit", "edit", "Cancel")]
    [DataRow("activities-create", "add", "Cancel")]
    [DataRow("activities-edit", "edit", "Cancel")]
    [DataRow("activities-details", "details", "Close")]
    [DataRow("themes-create", "add", "Cancel")]
    [DataRow("themes-edit", "edit", "Cancel")]
    [DataRow("holidays-create", "add", "Cancel")]
    [DataRow("holidays-edit", "edit", "Cancel")]
    public async Task ClosingADialog_ReturnsFocusToItsOpener(string form, string keyKind, string closeText)
    {
        var page = await OpenDialog(form);

        var key = Remembered();
        var opener = page.FindAll($"[data-focus-key='{key}']").Single();
        Assert.IsTrue(key == "add" || key.StartsWith(keyKind + "-"), key);
        Assert.IsTrue(key == "add" ? opener.TextContent.Trim().Length > 0 : opener.GetAttribute("title") is not null);
        harness.JSInterop.VerifyNotInvoke("emFocus.restore");

        await page.FormDialog().Button(closeText).ClickAsync(new());

        Assert.IsFalse(page.HasFormDialog());
        harness.JSInterop.VerifyInvoke("emFocus.restore", 1);
        harness.JSInterop.VerifyInvoke("emFocus.remember", 1);
    }

    [TestMethod]
    [DataRow("themes-edit")]
    [DataRow("games-create")]
    [DataRow("events-edit")]
    [DataRow("activities-details")]
    public async Task DismissWithoutChanges_ReturnsFocus(string form)
    {
        var page = await OpenDialog(form);
        var dialog = page.FormDialog();

        await dialog.TriggerEventAsync("ondialogdismiss", new DialogEventArgs { Id = dialog.Id, Reason = "dismiss" });

        Assert.IsFalse(page.HasFormDialog());
        harness.JSInterop.VerifyInvoke("emFocus.restore", 1);
    }

    [TestMethod]
    [DataRow("themes")]
    [DataRow("events")]
    public async Task Discard_ReturnsFocusToTheEditButton_NotTheQuestion(string route)
    {
        var page = await OpenDialog(route + "-edit");
        var key = Remembered();
        var field = route == "events"
            ? page.FormDialog().QuerySelector("fluent-text-field[placeholder='Enter event name...']")!
            : page.FormDialog().QuerySelector("fluent-text-field")!;
        await field.ChangeAsync(new ChangeEventArgs { Value = "Typed but not saved" });
        var dialog = page.FormDialog();

        await dialog.TriggerEventAsync("ondialogdismiss", new DialogEventArgs { Id = dialog.Id, Reason = "dismiss" });
        Assert.IsTrue(page.HasConfirmation());
        // Keep editing: focus stays in the form dialog, nothing is restored yet.
        await page.Confirmation().Button("Keep editing").ClickAsync(new());
        harness.JSInterop.VerifyNotInvoke("emFocus.restore");

        await page.FormDialog().TriggerEventAsync("ondialogdismiss", new DialogEventArgs { Id = page.FormDialog().Id, Reason = "dismiss" });
        await page.ConfirmAsync("Discard");

        Assert.IsFalse(page.HasFormDialog());
        // The question over the dialog never replaced the remembered Edit button.
        var remembered = harness.JSInterop.VerifyInvoke("emFocus.remember", 1);
        Assert.AreEqual(key, remembered[0].Arguments[0]);
        harness.JSInterop.VerifyInvoke("emFocus.restore", 1);
    }

    [TestMethod]
    [DataRow("events", "Game night")]
    [DataRow("games", "Beta")]
    [DataRow("activities", "Plain")]
    [DataRow("themes", "Spooky")]
    [DataRow("holidays", "Halloween")]
    public async Task DeleteConfirmation_CancelReturnsFocusToDeleteButton(string route, string item)
    {
        var page = Open(route);
        var delete = Row(page, item).QuerySelector("fluent-button[title=Delete]")!;
        var key = delete.GetAttribute("data-focus-key");

        await delete.ClickAsync(new());

        Assert.AreEqual(key, Assert.ContainsSingle(harness.JSInterop.VerifyInvoke("emFocus.remember", 1)).Arguments[0]);
        await page.Confirmation().Button("Cancel").ClickAsync(new());
        Assert.IsFalse(page.HasConfirmation());
        harness.JSInterop.VerifyInvoke("emFocus.restore", 1);
    }

    [TestMethod]
    public async Task DeleteConfirmed_RestoresFocusAfterTheDelete()
    {
        var page = Open("themes");
        api.Status(HttpMethod.Delete, $"/api/themes/{spooky.Id}", System.Net.HttpStatusCode.NoContent)
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>());

        await Row(page, "Spooky").QuerySelector("fluent-button[title=Delete]")!.ClickAsync(new());
        await page.ConfirmAsync();

        Assert.AreEqual(1, api.Count(HttpMethod.Delete, $"/api/themes/{spooky.Id}"));
        harness.JSInterop.VerifyInvoke("emFocus.restore", 1);
    }

    [TestMethod]
    public async Task SaveThatClosesTheDialog_RestoresFocus()
    {
        var page = Open("games");
        api.Status(HttpMethod.Put, $"/api/games/{alpha.Id}", System.Net.HttpStatusCode.OK);

        await Row(page, "Alpha").QuerySelector("fluent-button[title=Edit]")!.ClickAsync(new());
        await page.FormDialog().QuerySelector("form")!.SubmitAsync();

        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        Assert.AreEqual(1, api.Count(HttpMethod.Put, $"/api/games/{alpha.Id}"));
        harness.JSInterop.VerifyInvoke("emFocus.restore", 1);
    }

    [TestMethod]
    public async Task FocusReturn_SwallowsJavaScriptFailures()
    {
        var js = new BunitJSInterop { Mode = JSRuntimeMode.Strict };
        js.SetupVoid("emFocus.remember", _ => true).SetException(new JSException("emFocus is not defined"));
        js.SetupVoid("emFocus.restore").SetException(new JSDisconnectedException("gone"));
        var focus = new FocusReturn(js.JSRuntime);

        await focus.RememberAsync("edit-1");
        await focus.RestoreAsync();

        Assert.AreEqual("edit-1", Assert.ContainsSingle(js.Invocations["emFocus.remember"]).Arguments[0]);
        Assert.ContainsSingle(js.Invocations["emFocus.restore"]);
    }

    // ---- Helpers -------------------------------------------------------------------------------

    // Renders a page and waits until every list it loads has arrived (tags and the Events dialog
    // need the Theme and Holiday lists, not only the page's own).
    private IRenderedComponent<IComponent> Open(string route)
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

    private async Task<IRenderedComponent<IComponent>> OpenDialog(string form)
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
            case "events-edit": await RowButton(page, "Game night", "Edit"); break;
            case "events-details": await RowButton(page, "Game night", "Details"); break;
            case "games-edit": await RowButton(page, "Alpha", "Edit"); break;
            case "activities-edit": await RowButton(page, "Tagged", "Edit"); break;
            case "activities-details": await RowButton(page, "Tagged", "Details"); break;
            case "themes-edit": await RowButton(page, "Spooky", "Edit"); break;
            case "holidays-edit": await RowButton(page, "Halloween", "Edit"); break;
            default: throw new ArgumentOutOfRangeException(nameof(form), form);
        }
        Assert.IsTrue(page.HasFormDialog());
        return page;
    }

    private static Task RowButton(IRenderedComponent<IComponent> page, string item, string title) =>
        Row(page, item).QuerySelector($"fluent-button[title={title}]")!.ClickAsync(new());

    private static IElement Row(IRenderedComponent<IComponent> page, string item) =>
        page.FindAll("tbody tr.fluent-data-grid-row")
            .Where(r => r.Closest("fluent-dialog") is null)
            .Single(r => r.TextContent.Contains(item));

    // The key the page passed to emFocus.remember when its one dialog opened.
    private string Remembered() =>
        (string)Assert.ContainsSingle(harness.JSInterop.VerifyInvoke("emFocus.remember", 1)).Arguments[0]!;

    // Each role=group under root: its aria-label and the labels of its checkboxes.
    private static Dictionary<string, List<string>> Groups(IElement root) =>
        root.QuerySelectorAll("[role=group]").ToDictionary(
            g => g.GetAttribute("aria-label")!,
            g => g.QuerySelectorAll("fluent-checkbox").Select(c => c.TextContent.Trim()).ToList());

    // What a screen reader reads for each tag under root, in order.
    private static List<string> Tags(IElement root) =>
        root.QuerySelectorAll("fluent-badge.tag-badge").Select(Read).ToList();

    private static string Read(IElement badge) =>
        string.Join(" ", badge.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
