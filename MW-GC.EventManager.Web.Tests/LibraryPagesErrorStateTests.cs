using System.Net;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Tests;

// The Themes, Holidays and Activities pages rendered with bUnit against a stub API: a failed load
// shows a message with Retry, a failed write keeps the dialog or the list as it was and says why.
[TestClass]
public sealed class LibraryPagesErrorStateTests : IDisposable
{
    private const string ServerError = "The server had a problem (HTTP 500). Try again.";
    private const string Unreachable = "Could not reach the server. Check the connection and try again.";
    private readonly List<PageHarness> harnesses = [];

    public void Dispose()
    {
        foreach (var harness in harnesses) harness.Dispose();
    }

    public static IEnumerable<object?[]> TagPages => [["themes"], ["holidays"]];

    private sealed record TagPageUnderTest(IRenderedComponent<IComponent> Page, StubApiHandler Api, EntityBase Existing, string Name, string Singular);

    private TagPageUnderTest TagPage(string route, bool load = true)
    {
        var api = new StubApiHandler();
        EntityBase existing;
        if (route == "themes")
        {
            existing = new ThemeEntity { Name = "Spooky" };
            api.Json(HttpMethod.Get, "/api/themes", new[] { (ThemeEntity)existing });
        }
        else
        {
            existing = new HolidayEntity { Name = "Halloween" };
            api.Json(HttpMethod.Get, "/api/holidays", new[] { (HolidayEntity)existing });
        }
        var setUp = new TagPageUnderTest(null!, api, existing, route == "themes" ? "Themes" : "Holidays", route == "themes" ? "Theme" : "Holiday");
        return load ? Render(setUp) : setUp;
    }

    private TagPageUnderTest Render(TagPageUnderTest setUp)
    {
        var harness = new PageHarness(setUp.Api);
        harnesses.Add(harness);
        IRenderedComponent<IComponent> page = setUp.Name == "Themes" ? harness.Render<Themes>() : harness.Render<Holidays>();
        return setUp with { Page = page };
    }

    [TestMethod]
    [DynamicData(nameof(TagPages))]
    public async Task TagPageFailedLoadShowsMessageAndRetryLoadsTheList(string route)
    {
        var setUp = TagPage(route, load: false);
        setUp.Api.Offline(HttpMethod.Get, $"/api/{route}");

        var page = Render(setUp).Page;
        page.WaitForPageAlert();

        Assert.IsEmpty(page.Rows());
        Assert.IsEmpty(page.FindAll("fluent-progress-ring"));
        Assert.AreEqual($"{setUp.Name} could not be loaded. {Unreachable}", Assert.ContainsSingle(page.PageAlerts()));

        setUp.Api.Json(HttpMethod.Get, $"/api/{route}", route == "themes"
            ? new object[] { (ThemeEntity)setUp.Existing } : new object[] { (HolidayEntity)setUp.Existing });
        await page.Button("Retry").ClickAsync(new());

        page.WaitForRows(1);
        Assert.IsEmpty(page.PageAlerts());
    }

    [TestMethod]
    [DynamicData(nameof(TagPages))]
    public async Task TagPageFailedCreateKeepsDialogAndName(string route)
    {
        var (page, api, _, _, singular) = Loaded(route);
        api.Status(HttpMethod.Post, $"/api/{route}", HttpStatusCode.InternalServerError);
        await page.Button($"Add {singular}").ClickAsync(new());
        await page.Find("fluent-dialog fluent-text-field").ChangeAsync(new ChangeEventArgs { Value = "Typed name" });

        await page.Find("fluent-dialog form").SubmitAsync();

        Assert.AreEqual($"Add {singular}", page.DialogTitle());
        Assert.AreEqual("Typed name", page.Find("fluent-dialog fluent-text-field").GetAttribute("value"));
        Assert.Contains("Typed name", Assert.ContainsSingle(api.Bodies(HttpMethod.Post, $"/api/{route}"))!);
        Assert.AreEqual(ServerError, Assert.ContainsSingle(page.Dialog().Alerts()));
        Assert.IsFalse(page.Dialog().Button($"Add {singular}").IsBusy());

        // Cancel clears the message so the next dialog starts clean.
        await page.Dialog().Button("Cancel").ClickAsync(new());
        Assert.IsFalse(page.HasDialog());
        await page.Button($"Add {singular}").ClickAsync(new());
        Assert.IsEmpty(page.Dialog().Alerts());
    }

    [TestMethod]
    [DynamicData(nameof(TagPages))]
    public async Task TagPageFailedUpdateKeepsEditDialog(string route)
    {
        var (page, api, existing, _, singular) = Loaded(route);
        api.Offline(HttpMethod.Put, $"/api/{route}/{existing.Id}");
        await page.Find("fluent-button[title=Edit]").ClickAsync(new());

        await page.Find("fluent-dialog form").SubmitAsync();

        Assert.AreEqual($"Edit {singular}", page.DialogTitle());
        Assert.AreEqual(1, api.Count(HttpMethod.Put, $"/api/{route}/{existing.Id}"));
        Assert.AreEqual(Unreachable, Assert.ContainsSingle(page.Dialog().Alerts()));
    }

    [TestMethod]
    [DynamicData(nameof(TagPages))]
    public async Task TagPageFailedDeleteLeavesTheList(string route)
    {
        var (page, api, existing, _, singular) = Loaded(route);
        api.Status(HttpMethod.Delete, $"/api/{route}/{existing.Id}", HttpStatusCode.NotFound);
        var reads = api.Count(HttpMethod.Get, $"/api/{route}");

        await page.Find("fluent-button[title=Delete]").ClickAsync(new());

        Assert.AreEqual(1, api.Count(HttpMethod.Delete, $"/api/{route}/{existing.Id}"));
        Assert.ContainsSingle(page.Rows());
        Assert.AreEqual(reads, api.Count(HttpMethod.Get, $"/api/{route}"));
        Assert.AreEqual($"The {singular.ToLowerInvariant()} was not deleted. Not found. It may have been deleted.", Assert.ContainsSingle(page.PageAlerts()));
    }

    private TagPageUnderTest Loaded(string route)
    {
        var setUp = TagPage(route);
        setUp.Page.WaitForRows(1);
        return setUp;
    }

    private (StubApiHandler Api, ActivityEntity Existing) ActivitiesApi()
    {
        var game = new GameEntity { Name = "Game" };
        var existing = new ActivityEntity { Name = "Race", GameId = game.Id, Comments = "Old note" };
        var api = new StubApiHandler()
            .Json(HttpMethod.Get, "/api/activities", new[] { existing })
            .Json(HttpMethod.Get, "/api/games", new[] { game })
            .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
            .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>());
        return (api, existing);
    }

    private IRenderedComponent<Activities> RenderActivities(StubApiHandler api)
    {
        var harness = new PageHarness(api);
        harnesses.Add(harness);
        return harness.Render<Activities>();
    }

    private (IRenderedComponent<Activities> Page, StubApiHandler Api, ActivityEntity Existing) ActivitiesPage()
    {
        var (api, existing) = ActivitiesApi();
        var page = RenderActivities(api);
        page.WaitForRows(1);
        return (page, api, existing);
    }

    [TestMethod]
    public async Task ActivitiesFailedLoadShowsMessageAndRetryLoadsTheList()
    {
        var (api, existing) = ActivitiesApi();
        api.Status(HttpMethod.Get, "/api/activities", HttpStatusCode.BadGateway);

        var page = RenderActivities(api);
        page.WaitForPageAlert();

        Assert.IsEmpty(page.Rows());
        Assert.IsEmpty(page.FindAll("fluent-progress-ring"));
        Assert.AreEqual("Activities could not be loaded. The server had a problem (HTTP 502). Try again.", Assert.ContainsSingle(page.PageAlerts()));

        api.Json(HttpMethod.Get, "/api/activities", new[] { existing });
        await page.Button("Retry").ClickAsync(new());

        page.WaitForRows(1);
        Assert.IsEmpty(page.PageAlerts());
    }

    [TestMethod]
    public async Task ActivitiesFailedCreateKeepsDialogAndEnteredData()
    {
        var (page, api, existing) = ActivitiesPage();
        api.Status(HttpMethod.Post, "/api/activities", HttpStatusCode.InternalServerError);
        await page.Button("Add Activity").ClickAsync(new());
        await page.Find("fluent-dialog fluent-option").ClickAsync(new());
        await page.Find("fluent-dialog fluent-text-field").ChangeAsync(new ChangeEventArgs { Value = "New race" });
        await RulesBox(page).ChangeAsync(new ChangeEventArgs { Value = "No shortcuts" });

        await page.Find("fluent-dialog form").SubmitAsync();

        Assert.AreEqual("Add Activity", page.DialogTitle());
        Assert.AreEqual("New race", page.Find("fluent-dialog fluent-text-field").GetAttribute("value"));
        Assert.AreEqual("No shortcuts", RulesBox(page).GetAttribute("value"));
        Assert.AreEqual(existing.GameId.ToString(), page.Find("fluent-dialog fluent-select").GetAttribute("current-value"));
        var sent = Assert.ContainsSingle(api.Bodies(HttpMethod.Post, "/api/activities"))!;
        Assert.Contains("No shortcuts", sent);
        Assert.Contains(existing.GameId.ToString(), sent);
        Assert.AreEqual(ServerError, Assert.ContainsSingle(page.Dialog().Alerts()));
    }

    [TestMethod]
    [DataRow("Duplicate", "duplicate", "duplicated")]
    [DataRow("Delete", "", "deleted")]
    public async Task ActivitiesFailedDuplicateOrDeleteLeavesTheList(string handler, string suffix, string verb)
    {
        var (page, api, existing) = ActivitiesPage();
        var path = suffix.Length == 0 ? $"/api/activities/{existing.Id}" : $"/api/activities/{existing.Id}/{suffix}";
        var method = handler == "Delete" ? HttpMethod.Delete : HttpMethod.Post;
        api.Status(method, path, HttpStatusCode.InternalServerError);
        var reads = api.Count(HttpMethod.Get, "/api/activities");

        await page.Find($"fluent-button[title={handler}]").ClickAsync(new());

        Assert.AreEqual(1, api.Count(method, path));
        Assert.Contains("Race", Assert.ContainsSingle(page.Rows()));
        Assert.AreEqual(reads, api.Count(HttpMethod.Get, "/api/activities"));
        Assert.AreEqual($"The activity was not {verb}. {ServerError}", Assert.ContainsSingle(page.PageAlerts()));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ActivitiesFailedCommentSaveKeepsTextAndButton(bool offline)
    {
        var (page, api, existing) = ActivitiesPage();
        var path = $"/api/activities/{existing.Id}/comments";
        if (offline) api.Offline(HttpMethod.Patch, path);
        else api.Status(HttpMethod.Patch, path, HttpStatusCode.InternalServerError);
        await page.Find("fluent-button[title=Details]").ClickAsync(new());
        Assert.AreEqual("Old note", page.Find("fluent-dialog fluent-text-area").GetAttribute("value"));
        Assert.IsTrue(page.Dialog().Button("Save Comments").IsBusy());
        await page.Find("fluent-dialog fluent-text-area").ChangeAsync(new ChangeEventArgs { Value = "Typed but not saved" });
        var reads = api.Count(HttpMethod.Get, "/api/activities");

        await page.Dialog().Button("Save Comments").ClickAsync(new());

        Assert.Contains("Typed but not saved", Assert.ContainsSingle(api.Bodies(HttpMethod.Patch, path))!);
        Assert.AreEqual("Typed but not saved", page.Find("fluent-dialog fluent-text-area").GetAttribute("value"));
        Assert.IsFalse(page.Dialog().Button("Save Comments").IsBusy());
        Assert.AreEqual(reads, api.Count(HttpMethod.Get, "/api/activities"));
        Assert.AreEqual(ApiResult.CommentsNotSaved(offline ? Unreachable : ServerError), Assert.ContainsSingle(page.Dialog().Alerts()));

        // No false success: the stored comment is unchanged, so reopening the dialog shows the old note.
        await page.Dialog().Button("Close").ClickAsync(new());
        await page.Find("fluent-button[title=Details]").ClickAsync(new());
        Assert.AreEqual("Old note", page.Find("fluent-dialog fluent-text-area").GetAttribute("value"));
        await page.Find("fluent-dialog fluent-text-area").ChangeAsync(new ChangeEventArgs { Value = "Typed but not saved" });

        // A retry that succeeds clears the error and stores the text.
        api.Status(HttpMethod.Patch, path, HttpStatusCode.NoContent);
        await page.Dialog().Button("Save Comments").ClickAsync(new());
        Assert.AreEqual(2, api.Count(HttpMethod.Patch, path));
        Assert.Contains("Typed but not saved", api.Bodies(HttpMethod.Patch, path)[1]!);
        Assert.IsEmpty(page.Dialog().Alerts());
        Assert.AreEqual("Typed but not saved", page.Find("fluent-dialog fluent-text-area").GetAttribute("value"));
        Assert.IsTrue(page.Dialog().Button("Save Comments").IsBusy());
        Assert.AreEqual(reads + 1, api.Count(HttpMethod.Get, "/api/activities"));
    }

    private static IElement RulesBox(IRenderedComponent<Activities> page) =>
        page.Find("fluent-dialog fluent-text-area[placeholder='Rules for this activity...']");
}
