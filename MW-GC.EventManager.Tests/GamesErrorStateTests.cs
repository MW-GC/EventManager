using System.Net;
using System.Reflection;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using MW_GC.EventManager.Web.Services;
using Xunit;

namespace MW_GC.EventManager.Tests;

// Games page handlers against a stub API: failures show a message and never a false success.
public class GamesErrorStateTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Games page = new();
    private readonly StubApiHandler api = new();
    private readonly GameEntity existing = new() { Name = "Existing game" };

    public GamesErrorStateTests()
    {
        api.Json(HttpMethod.Get, "/api/games", new[] { existing })
           .Json(HttpMethod.Get, "/api/activities", Array.Empty<ActivityEntity>())
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>());
        var http = api.Client();
        Inject("GameSvc", new GameService(http));
        Inject("ActivitySvc", new ActivityService(http));
        Inject("ThemeSvc", new ThemeService(http));
        Inject("HolidaySvc", new HolidayService(http));
    }

    [Fact]
    public async Task CreateThatGets500KeepsTheDialogOpenWithTheTypedNameAndShowsTheError()
    {
        await Init();
        api.Status(HttpMethod.Post, "/api/games", HttpStatusCode.InternalServerError, "System.Exception: storage down");
        Call("ShowCreate");
        var typed = (GameEntity)Get("_create")!;
        typed.Name = "Typed game";
        typed.Website = "https://example.test/typed";
        var readsBefore = api.Count(HttpMethod.Get, "/api/games");

        await (Task)Call("CreateGame")!;

        Assert.Equal(true, Get("_showCreate"));
        Assert.Same(typed, Get("_create"));
        Assert.Equal("Typed game", typed.Name);
        Assert.Equal("https://example.test/typed", typed.Website);
        Assert.Equal("The server had a problem (HTTP 500). Try again.", Get("_dialogError"));
        Assert.Equal(false, Get("_saving"));
        Assert.Equal(readsBefore, api.Count(HttpMethod.Get, "/api/games"));
        Assert.Equal(existing.Id, Assert.Single(Games).Id);
    }

    [Fact]
    public async Task CreateWithTheApiUnreachableKeepsTheDialogOpenAndShowsTheError()
    {
        await Init();
        api.Offline(HttpMethod.Post, "/api/games");
        Call("ShowCreate");
        ((GameEntity)Get("_create")!).Name = "Typed game";

        await (Task)Call("CreateGame")!;

        Assert.Equal(true, Get("_showCreate"));
        Assert.Equal("Typed game", ((GameEntity)Get("_create")!).Name);
        Assert.Equal("Could not reach the server. Check the connection and try again.", Get("_dialogError"));
        Assert.DoesNotContain("refused", (string)Get("_dialogError")!);
        Assert.Equal(false, Get("_saving"));
    }

    [Fact]
    public async Task CreateRejectedByTheApiShowsItsOwnText()
    {
        await Init();
        api.Status(HttpMethod.Post, "/api/games", HttpStatusCode.BadRequest, "Name is required.");
        Call("ShowCreate");
        await (Task)Call("CreateGame")!;
        Assert.Equal("Name is required.", Get("_dialogError"));
        Assert.Equal(true, Get("_showCreate"));
    }

    [Fact]
    public async Task SuccessfulCreateAfterAFailureClosesTheDialogAndReloads()
    {
        await Init();
        api.Status(HttpMethod.Post, "/api/games", HttpStatusCode.InternalServerError);
        Call("ShowCreate");
        ((GameEntity)Get("_create")!).Name = "Typed game";
        await (Task)Call("CreateGame")!;
        Assert.NotNull(Get("_dialogError"));

        var created = new GameEntity { Name = "Typed game" };
        api.Json(HttpMethod.Post, "/api/games", created)
           .Json(HttpMethod.Get, "/api/games", new[] { existing, created });
        await (Task)Call("CreateGame")!;

        Assert.Equal(false, Get("_showCreate"));
        Assert.Null(Get("_dialogError"));
        Assert.Equal(2, Games.Count);
        Assert.Null(LoadMessage());
    }

    [Fact]
    public async Task UpdateThatFailsKeepsTheEditDialogAndTheEditedName()
    {
        await Init();
        api.Status(HttpMethod.Put, $"/api/games/{existing.Id}", HttpStatusCode.ServiceUnavailable);
        Call("ShowEdit", existing);
        ((GameEntity)Get("_edit")!).Name = "Renamed";

        await (Task)Call("SaveEdit")!;

        Assert.Equal(existing.Id, Get("_editingId"));
        Assert.Equal("Renamed", ((GameEntity)Get("_edit")!).Name);
        Assert.Equal("The server had a problem (HTTP 503). Try again.", Get("_dialogError"));
        Assert.Equal("Existing game", Assert.Single(Games).Name);
    }

    [Fact]
    public async Task DeleteThatFailsLeavesTheListAndShowsTheError()
    {
        await Init();
        api.Status(HttpMethod.Delete, $"/api/games/{existing.Id}", HttpStatusCode.InternalServerError);
        var list = Games;
        var readsBefore = api.Count(HttpMethod.Get, "/api/games");

        await (Task)Call("Delete", existing.Id)!;

        Assert.Same(list, Games);
        Assert.Equal(existing.Id, Assert.Single(Games).Id);
        Assert.Equal(readsBefore, api.Count(HttpMethod.Get, "/api/games"));
        Assert.Equal("The game was not deleted. The server had a problem (HTTP 500). Try again.", Get("_actionError"));
    }

    [Fact]
    public async Task FailedLoadShowsTheMessageNotTheSpinnerAndRetryLoadsTheList()
    {
        api.Offline(HttpMethod.Get, "/api/games");

        await Init();

        Assert.Null(Get("_games"));
        var loads = (LoadErrors)Get("_loads")!;
        Assert.True(loads.Failed("Games"));
        Assert.Equal("Games could not be loaded. Could not reach the server. Check the connection and try again.", LoadMessage());

        api.Json(HttpMethod.Get, "/api/games", new[] { existing });
        await (Task)Call("LoadAll")!;

        Assert.False(loads.Failed("Games"));
        Assert.Null(LoadMessage());
        Assert.Equal(existing.Id, Assert.Single(Games).Id);
    }

    [Fact]
    public async Task OneFailedLookupStillShowsTheGamesAndNamesTheList()
    {
        api.Status(HttpMethod.Get, "/api/themes", HttpStatusCode.InternalServerError);

        await Init();

        Assert.Equal(existing.Id, Assert.Single(Games).Id);
        Assert.Equal("Themes could not be loaded. The server had a problem (HTTP 500). Try again.", LoadMessage());
    }

    private List<GameEntity> Games => (List<GameEntity>)Get("_games")!;
    private string? LoadMessage() => ((LoadErrors)Get("_loads")!).Message;
    private Task Init() => (Task)typeof(Games).GetMethod("OnInitializedAsync", Private)!.Invoke(page, null)!;
    private void Inject(string name, object service) => typeof(Games).GetProperty(name, Private)!.SetValue(page, service);
    private object? Get(string name) => typeof(Games).GetField(name, Private)!.GetValue(page);
    private object? Call(string name, params object[] args) => typeof(Games).GetMethod(name, Private)!.Invoke(page, args);
}
