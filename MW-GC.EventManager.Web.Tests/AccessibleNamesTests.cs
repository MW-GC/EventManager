using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using MW_GC.EventManager.Web.Pages;
using MW_GC.EventManager.Web.Shared;

namespace MW_GC.EventManager.Web.Tests;

// Issue #54 on all five pages, rendered with bUnit against a stub API: icon-only buttons, searches,
// pickers and dialogs carry accessible names; view toggles say which is on; Theme and Holiday tags
// differ by more than colour; the page title is a focusable h1 with a matching tab title. That every
// dialog returns focus to its opener is in FocusReturnTests; the pages are opened through AccessibleNamesFixture.
[TestClass]
public sealed class AccessibleNamesTests : AccessibleNamesFixture
{
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

    // ---- Helpers -------------------------------------------------------------------------------

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
