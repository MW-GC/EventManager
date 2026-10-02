using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using MW_GC.EventManager.Web.Services;
using Xunit;

namespace MW_GC.EventManager.Tests;

// Themes, Holidays and Activities handlers against a stub API: a failed load shows a message
// with Retry, a failed write keeps the dialog or the list as it was and says why.
public class LibraryPagesErrorStateTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string ServerError = "The server had a problem (HTTP 500). Try again.";
    private const string Unreachable = "Could not reach the server. Check the connection and try again.";

    private static void Inject(ComponentBase page, HttpClient http)
    {
        foreach (var property in page.GetType().GetProperties(Private))
        {
            if (property.PropertyType == typeof(GameService)) property.SetValue(page, new GameService(http));
            if (property.PropertyType == typeof(ActivityService)) property.SetValue(page, new ActivityService(http));
            if (property.PropertyType == typeof(ThemeService)) property.SetValue(page, new ThemeService(http));
            if (property.PropertyType == typeof(HolidayService)) property.SetValue(page, new HolidayService(http));
        }
    }

    private static object? Get(object page, string name) => page.GetType().GetField(name, Private)!.GetValue(page);
    private static void Set(object page, string name, object? value) => page.GetType().GetField(name, Private)!.SetValue(page, value);
    private static object? Call(object page, string name, params object[] args) => page.GetType().GetMethod(name, Private)!.Invoke(page, args);
    private static Task CallAsync(object page, string name, params object[] args) => (Task)Call(page, name, args)!;
    private static LoadErrors Loads(object page) => (LoadErrors)Get(page, "_loads")!;

    public static TheoryData<string> TagPages => new() { "themes", "holidays" };

    private static (ComponentBase Page, StubApiHandler Api, EntityBase Existing, string Field, Func<string, EntityBase> New) TagPage(string route)
    {
        var api = new StubApiHandler();
        if (route == "themes")
        {
            var theme = new ThemeEntity { Name = "Spooky" };
            api.Json(HttpMethod.Get, "/api/themes", new[] { theme });
            var page = new Themes();
            Inject(page, api.Client());
            return (page, api, theme, "_themes", name => new ThemeEntity { Name = name });
        }
        var holiday = new HolidayEntity { Name = "Halloween" };
        api.Json(HttpMethod.Get, "/api/holidays", new[] { holiday });
        var holidays = new Holidays();
        Inject(holidays, api.Client());
        return (holidays, api, holiday, "_holidays", name => new HolidayEntity { Name = name });
    }

    [Theory]
    [MemberData(nameof(TagPages))]
    public async Task TagPageFailedLoadShowsMessageAndRetryLoadsTheList(string route)
    {
        var (page, api, existing, field, _) = TagPage(route);
        var name = route == "themes" ? "Themes" : "Holidays";
        api.Offline(HttpMethod.Get, $"/api/{route}");

        await CallAsync(page, "OnInitializedAsync");

        Assert.Null(Get(page, field));
        Assert.True(Loads(page).Failed(name));
        Assert.Equal($"{name} could not be loaded. {Unreachable}", Loads(page).Message);

        api.Json(HttpMethod.Get, $"/api/{route}", new[] { existing });
        await CallAsync(page, "Reload");

        Assert.Null(Loads(page).Message);
        Assert.Single((System.Collections.IList)Get(page, field)!);
    }

    [Theory]
    [MemberData(nameof(TagPages))]
    public async Task TagPageFailedCreateKeepsDialogAndName(string route)
    {
        var (page, api, _, _, _) = TagPage(route);
        await CallAsync(page, "OnInitializedAsync");
        api.Status(HttpMethod.Post, $"/api/{route}", HttpStatusCode.InternalServerError);
        Call(page, "ShowCreate");
        var typed = (EntityBase)Get(page, "_create")!;
        typed.GetType().GetProperty("Name")!.SetValue(typed, "Typed name");

        await CallAsync(page, "Create");

        Assert.Equal(true, Get(page, "_showCreate"));
        Assert.Same(typed, Get(page, "_create"));
        Assert.Equal("Typed name", typed.GetType().GetProperty("Name")!.GetValue(typed));
        Assert.Equal(ServerError, Get(page, "_dialogError"));
        Assert.Equal(false, Get(page, "_saving"));

        // Cancel clears the message so the next dialog starts clean.
        Call(page, "HideCreate");
        Assert.Null(Get(page, "_dialogError"));
    }

    [Theory]
    [MemberData(nameof(TagPages))]
    public async Task TagPageFailedUpdateKeepsEditDialog(string route)
    {
        var (page, api, existing, _, _) = TagPage(route);
        await CallAsync(page, "OnInitializedAsync");
        api.Offline(HttpMethod.Put, $"/api/{route}/{existing.Id}");
        Call(page, "ShowEdit", existing);

        await CallAsync(page, "SaveEdit");

        Assert.Equal(existing.Id, Get(page, "_editingId"));
        Assert.Equal(Unreachable, Get(page, "_dialogError"));
    }

    [Theory]
    [MemberData(nameof(TagPages))]
    public async Task TagPageFailedDeleteLeavesTheList(string route)
    {
        var (page, api, existing, field, _) = TagPage(route);
        await CallAsync(page, "OnInitializedAsync");
        api.Status(HttpMethod.Delete, $"/api/{route}/{existing.Id}", HttpStatusCode.NotFound);
        var list = Get(page, field);
        var reads = api.Count(HttpMethod.Get, $"/api/{route}");

        await CallAsync(page, "Delete", existing.Id);

        Assert.Same(list, Get(page, field));
        Assert.Single((System.Collections.IList)list!);
        Assert.Equal(reads, api.Count(HttpMethod.Get, $"/api/{route}"));
        var singular = route == "themes" ? "theme" : "holiday";
        Assert.Equal($"The {singular} was not deleted. Not found. It may have been deleted.", Get(page, "_actionError"));
    }

    private static (Activities Page, StubApiHandler Api, ActivityEntity Existing) ActivitiesPage()
    {
        var game = new GameEntity { Name = "Game" };
        var existing = new ActivityEntity { Name = "Race", GameId = game.Id, Comments = "Old note" };
        var api = new StubApiHandler()
            .Json(HttpMethod.Get, "/api/activities", new[] { existing })
            .Json(HttpMethod.Get, "/api/games", new[] { game })
            .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
            .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>());
        var page = new Activities();
        Inject(page, api.Client());
        return (page, api, existing);
    }

    [Fact]
    public async Task ActivitiesFailedLoadShowsMessageAndRetryLoadsTheList()
    {
        var (page, api, existing) = ActivitiesPage();
        api.Status(HttpMethod.Get, "/api/activities", HttpStatusCode.BadGateway);

        await CallAsync(page, "OnInitializedAsync");

        Assert.Null(Get(page, "_activities"));
        Assert.Equal("Activities could not be loaded. The server had a problem (HTTP 502). Try again.", Loads(page).Message);

        api.Json(HttpMethod.Get, "/api/activities", new[] { existing });
        await CallAsync(page, "LoadAll");
        Assert.Null(Loads(page).Message);
        Assert.Single((List<ActivityEntity>)Get(page, "_activities")!);
    }

    [Fact]
    public async Task ActivitiesFailedCreateKeepsDialogAndEnteredData()
    {
        var (page, api, existing) = ActivitiesPage();
        await CallAsync(page, "OnInitializedAsync");
        api.Status(HttpMethod.Post, "/api/activities", HttpStatusCode.InternalServerError);
        Call(page, "ShowCreate");
        var typed = (ActivityEntity)Get(page, "_create")!;
        typed.Name = "New race";
        typed.Rules = "No shortcuts";
        typed.GameId = existing.GameId;

        await CallAsync(page, "CreateActivity");

        Assert.Equal(true, Get(page, "_showCreate"));
        Assert.Same(typed, Get(page, "_create"));
        Assert.Equal("No shortcuts", typed.Rules);
        Assert.Equal(ServerError, Get(page, "_dialogError"));
    }

    [Theory]
    [InlineData("Duplicate", "duplicate", "duplicated")]
    [InlineData("Delete", "", "deleted")]
    public async Task ActivitiesFailedDuplicateOrDeleteLeavesTheList(string handler, string suffix, string verb)
    {
        var (page, api, existing) = ActivitiesPage();
        await CallAsync(page, "OnInitializedAsync");
        var path = suffix.Length == 0 ? $"/api/activities/{existing.Id}" : $"/api/activities/{existing.Id}/{suffix}";
        api.Status(handler == "Delete" ? HttpMethod.Delete : HttpMethod.Post, path, HttpStatusCode.InternalServerError);
        var list = Get(page, "_activities");
        var reads = api.Count(HttpMethod.Get, "/api/activities");

        await CallAsync(page, handler, existing.Id);

        Assert.Same(list, Get(page, "_activities"));
        Assert.Single((List<ActivityEntity>)list!);
        Assert.Equal(reads, api.Count(HttpMethod.Get, "/api/activities"));
        Assert.Equal($"The activity was not {verb}. {ServerError}", Get(page, "_actionError"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivitiesFailedCommentSaveKeepsTextAndButton(bool offline)
    {
        var (page, api, existing) = ActivitiesPage();
        await CallAsync(page, "OnInitializedAsync");
        var path = $"/api/activities/{existing.Id}/comments";
        if (offline) api.Offline(HttpMethod.Patch, path);
        else api.Status(HttpMethod.Patch, path, HttpStatusCode.InternalServerError);
        Call(page, "ShowDetails", existing);
        Set(page, "_detailComments", "Typed but not saved");
        Call(page, "OnDetailCommentsChanged");

        await CallAsync(page, "SaveDetailComments");

        Assert.Equal("Typed but not saved", Get(page, "_detailComments"));
        Assert.Equal(true, Get(page, "_detailCommentsDirty"));
        Assert.Equal(false, Get(page, "_savingComments"));
        Assert.Equal("Old note", existing.Comments);
        Assert.Equal(ApiResult.CommentsNotSaved(offline ? Unreachable : ServerError), Get(page, "_commentsError"));

        // A retry that succeeds clears the error and stores the text.
        api.Status(HttpMethod.Patch, path, HttpStatusCode.NoContent);
        await CallAsync(page, "SaveDetailComments");
        Assert.Null(Get(page, "_commentsError"));
        Assert.Equal(false, Get(page, "_detailCommentsDirty"));
        Assert.Equal("Typed but not saved", existing.Comments);
    }
}
