using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components.Icons.Regular;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;

namespace MW_GC.EventManager.Web.Tests;

// Issue #124: the shared Library page's list: load, error and Retry, search, the card view, the delete confirmation
// and the busy guard. The editor is in LibraryEditorTests, the Games Website in GamesWebsiteTests and the generic
// client in LibraryClientTests (#196.10).
[TestClass]
public sealed class LibraryPageTests : LibraryPageFixture
{
    // ---- Load, error and retry -------------------------------------------------------------------

    [TestMethod]
    public async Task FailedLoad_ShowsTheMessageNotTheSpinner_AndRetryLoadsTheList()
    {
        api.Offline(HttpMethod.Get, "/api/themes");

        var page = Themes(rows: 0);
        page.WaitForPageAlert();

        Assert.AreEqual("Themes could not be loaded. Could not reach the server. Check the connection and try again.", Assert.ContainsSingle(page.PageAlerts()));
        Assert.DoesNotContain("Loading themes...", page.Markup);
        Assert.IsEmpty(page.FindAll("fluent-progress-ring"));
        Assert.IsEmpty(page.Rows());

        api.Json(HttpMethod.Get, "/api/themes", new[] { spooky, cozy });
        await page.Button("Retry").ClickAsync(new());

        page.WaitForRows(2);
        Assert.IsEmpty(page.PageAlerts());
    }

    // The client throws for a non-success status too (#159), not only when the server cannot be reached: the
    // page shows the server's message in the same error bar, with Retry.
    [TestMethod]
    public async Task FailedLoad_AServerError_ShowsTheServerMessage_AndRetryLoadsTheList()
    {
        api.Status(HttpMethod.Get, "/api/themes", HttpStatusCode.InternalServerError);

        var page = Themes(rows: 0);
        page.WaitForPageAlert();

        Assert.AreEqual("Themes could not be loaded. The server had a problem (HTTP 500). Try again.", Assert.ContainsSingle(page.PageAlerts()));
        Assert.IsEmpty(page.FindAll("fluent-progress-ring"));
        Assert.IsEmpty(page.Rows());

        api.Json(HttpMethod.Get, "/api/themes", new[] { spooky, cozy });
        await page.Button("Retry").ClickAsync(new());

        page.WaitForRows(2);
        Assert.IsEmpty(page.PageAlerts());
    }

    // ---- Search and card view --------------------------------------------------------------------

    [TestMethod]
    public async Task Search_FiltersByNameIgnoringCase_AndAClearedBoxRestoresTheList()
    {
        var page = Themes();

        await Search(page).InputAsync(new ChangeEventArgs { Value = "SPO" });
        page.WaitForAssertion(() => Assert.Contains("Spooky", Assert.ContainsSingle(page.Rows())), Settle);

        await Search(page).InputAsync(new ChangeEventArgs { Value = "" });
        page.WaitForAssertion(() => Assert.HasCount(2, page.Rows()), Settle);
        Assert.AreEqual("Search themes", Search(page).GetAttribute("aria-label"));
    }

    // #196.3: an item stored with no name counts as an empty name: a search leaves it out instead of throwing.
    [TestMethod]
    public async Task Search_AnItemWithNoName_CountsAsEmpty()
    {
        api.Json(HttpMethod.Get, "/api/themes", new[] { spooky, new ThemeEntity { Name = null! } });
        var page = Themes();

        await Search(page).InputAsync(new ChangeEventArgs { Value = "spo" });
        page.WaitForAssertion(() => Assert.Contains("Spooky", Assert.ContainsSingle(page.Rows())), Settle);
        await Search(page).InputAsync(new ChangeEventArgs { Value = "" });
        page.WaitForAssertion(() => Assert.HasCount(2, page.Rows()), Settle);
        Assert.IsFalse(harness.UnhandledException.IsCompleted);
    }

    // A page that gives neither a card template nor a card icon (the bare page, and Activities, which never had
    // cards) gets no view toggle, and still shows its rows; Games (its own template) and a page with a card icon get
    // both buttons (#196.5, #196.8).
    [TestMethod]
    public void ViewToggle_IsOfferedOnlyToAPageWithACardTemplateOrIcon()
    {
        var page = Themes();
        Assert.IsEmpty(page.FindAll("fluent-button[title='Card view']"));
        Assert.IsEmpty(page.FindAll("fluent-button[title='Table view']"));

        api.Json(HttpMethod.Get, "/api/activities", new[] { new ActivityEntity { Name = "Race", GameId = kart.Id } });
        var activities = harness.Render<Activities>();
        activities.WaitForAssertion(() => Assert.Contains("Race", Assert.ContainsSingle(activities.Rows())));
        Assert.IsEmpty(activities.FindAll("fluent-button[title='Card view']"));
        Assert.IsEmpty(activities.FindAll("fluent-button[title='Table view']"));

        var games = Games();
        Assert.ContainsSingle(games.FindAll("fluent-button[title='Card view']"));
        Assert.ContainsSingle(games.FindAll("fluent-button[title='Table view']"));

        var iconCards = Themes(cardIcon: new Size24.Color());
        Assert.ContainsSingle(iconCards.FindAll("fluent-button[title='Card view']"));
    }

    // The card view's Edit and Delete are the page's own: Edit opens that item, Delete asks and deletes it, and
    // while a delete runs every card's Delete and the busy card's Edit are disabled, as in the table.
    [TestMethod]
    public async Task CardView_EditAndDeleteFromACard_Work_AndAreDisabledWhileADeleteRuns()
    {
        var page = Themes(cardIcon: new Size24.Color());
        await page.Find("fluent-button[title='Card view']").ClickAsync(new());
        Assert.IsEmpty(page.Rows());

        await CardButton(page, "Cozy", "Edit").ClickAsync(new());
        Assert.AreEqual("Edit Theme", page.DialogTitle());
        Assert.AreEqual("Cozy", NameField(page).GetAttribute("value"));
        await page.FormDialog().Button("Cancel").ClickAsync(new());

        var path = $"/api/themes/{spooky.Id}";
        var gate = api.Hold(HttpMethod.Delete, path, () => new HttpResponseMessage(HttpStatusCode.NoContent));
        api.Json(HttpMethod.Get, "/api/themes", new[] { cozy });
        await CardButton(page, "Spooky", "Delete").ClickAsync(new());
        Assert.AreEqual("Delete Spooky? This cannot be undone.", Message(page.Confirmation()));
        var first = page.ConfirmAsync();

        page.WaitForAssertion(() =>
        {
            Assert.IsTrue(CardButton(page, "Spooky", "Delete").IsBusy());
            Assert.IsTrue(CardButton(page, "Cozy", "Delete").IsBusy());
            Assert.IsTrue(CardButton(page, "Spooky", "Edit").IsBusy());
            Assert.IsFalse(CardButton(page, "Cozy", "Edit").IsBusy());
        });
        // A click that lands anyway asks nothing.
        await CardButton(page, "Cozy", "Delete").ClickAsync(new());
        Assert.IsFalse(page.HasConfirmation());

        gate.SetResult();
        await first;

        Assert.AreEqual(1, api.Count(HttpMethod.Delete, path));
        Assert.HasCount(1, api.Requests.Where(r => r.Method == HttpMethod.Delete).ToList());
        page.WaitForAssertion(() => Assert.IsEmpty(page.FindAll("fluent-card fluent-card").Where(c => c.TextContent.Contains("Spooky")).ToList()));
        Assert.IsFalse(CardButton(page, "Cozy", "Delete").IsBusy());
    }

    // ---- Delete confirmation and the busy guard ----------------------------------------------------

    [TestMethod]
    public async Task Delete_NamesTheItem_CancelKeepsIt_ConfirmRemovesIt()
    {
        var page = Themes();
        var path = $"/api/themes/{spooky.Id}";

        await RowButton(page, "Spooky", "Delete").ClickAsync(new());
        Assert.AreEqual("Delete theme", page.Confirmation().GetAttribute("aria-label"));
        Assert.AreEqual("Delete Spooky? This cannot be undone.", Message(page.Confirmation()));
        await page.Confirmation().Button("Cancel").ClickAsync(new());

        Assert.IsFalse(page.HasConfirmation());
        AssertNoWrites();
        Assert.HasCount(2, page.Rows());

        api.Status(HttpMethod.Delete, path, HttpStatusCode.NoContent)
           .Json(HttpMethod.Get, "/api/themes", new[] { cozy });
        await RowButton(page, "Spooky", "Delete").ClickAsync(new());
        await page.ConfirmAsync();

        Assert.AreEqual(1, api.Count(HttpMethod.Delete, path));
        page.WaitForAssertion(() => Assert.Contains("Cozy", Assert.ContainsSingle(page.Rows())));
        Assert.IsFalse(page.HasConfirmation());
        Assert.IsEmpty(page.PageAlerts());
    }

    [TestMethod]
    public async Task Delete_ASecondClickWhileTheFirstIsInFlight_SendsNothing()
    {
        var page = Themes();
        var gate = api.Hold(HttpMethod.Delete, $"/api/themes/{spooky.Id}", () => new HttpResponseMessage(HttpStatusCode.NoContent));

        await RowButton(page, "Spooky", "Delete").ClickAsync(new());
        var first = page.ConfirmAsync();

        // While it runs: every Delete and the busy row's Edit are disabled.
        page.WaitForAssertion(() =>
        {
            Assert.IsTrue(page.FindAll("fluent-button[title=Delete]").All(b => b.IsBusy()));
            Assert.IsTrue(RowButton(page, "Spooky", "Edit").IsBusy());
            Assert.IsFalse(RowButton(page, "Cozy", "Edit").IsBusy());
        });
        // A click that lands anyway asks nothing and sends nothing.
        await RowButton(page, "Cozy", "Delete").ClickAsync(new());
        Assert.IsFalse(page.HasConfirmation());

        gate.SetResult();
        await first;

        Assert.HasCount(1, api.Requests.Where(r => r.Method == HttpMethod.Delete).ToList());
        page.WaitForAssertion(() => Assert.IsFalse(RowButton(page, "Cozy", "Delete").IsBusy()));
    }

    [TestMethod]
    public async Task Save_ASecondSubmitWhileTheFirstIsInFlight_SendsOneRequest()
    {
        var page = Themes();
        var gate = api.Hold(HttpMethod.Post, "/api/themes", () => new HttpResponseMessage(HttpStatusCode.Created));

        await page.Button("Add Theme").ClickAsync(new());
        await NameField(page).ChangeAsync(new ChangeEventArgs { Value = "Retro" });
        var first = Submit(page);
        page.WaitForAssertion(() => Assert.IsTrue(page.FormDialog().Button("Add Theme").IsBusy()));

        await Submit(page);
        gate.SetResult();
        await first;

        Assert.AreEqual(1, api.Count(HttpMethod.Post, "/api/themes"));
        Assert.IsFalse(page.HasFormDialog());
    }
}
