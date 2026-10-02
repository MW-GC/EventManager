using System.Net;
using System.Reflection;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Pages;
using MW_GC.EventManager.Web.Services;
using Xunit;

namespace MW_GC.EventManager.Tests;

// Events page handlers against a stub API: lookups fail independently, and Select Winner,
// Delete and Save Comments never report a success the API did not confirm.
public class EventsErrorStateTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string ServerError = "The server had a problem (HTTP 500). Try again.";
    private const string Unreachable = "Could not reach the server. Check the connection and try again.";
    private readonly Events page = new();
    private readonly StubApiHandler api = new();
    private readonly Guid gameA = Guid.NewGuid();
    private readonly Guid gameB = Guid.NewGuid();
    private readonly ActivityEntity first;
    private readonly ActivityEntity second;
    private readonly EventEntity ev;

    public EventsErrorStateTests()
    {
        first = new ActivityEntity { Name = "First", GameId = gameA, Comments = "Old note" };
        second = new ActivityEntity { Name = "Second", GameId = gameB };
        ev = new EventEntity
        {
            Name = "Game night",
            Selections =
            [
                new() { Game = new Game { Id = gameA, Name = "A" }, Activity = new Activity { Id = first.Id, GameId = gameA, Name = first.Name } },
                new() { Game = new Game { Id = gameB, Name = "B" }, Activity = new Activity { Id = second.Id, GameId = gameB, Name = second.Name } }
            ],
            WinnerActivityId = first.Id
        };
        api.Json(HttpMethod.Get, "/api/events", new[] { ev })
           .Json(HttpMethod.Get, "/api/games", new[] { new GameEntity { Id = gameA, Name = "A" }, new GameEntity { Id = gameB, Name = "B" } })
           .Json(HttpMethod.Get, "/api/activities", new[] { first, second })
           .Json(HttpMethod.Get, "/api/themes", new[] { new ThemeEntity { Name = "Spooky" } })
           .Json(HttpMethod.Get, "/api/holidays", new[] { new HolidayEntity { Name = "Halloween" } });
        var http = api.Client();
        Inject("EventSvc", new EventService(http));
        Inject("GameSvc", new GameService(http));
        Inject("ActivitySvc", new ActivityService(http));
        Inject("ThemeSvc", new ThemeService(http));
        Inject("HolidaySvc", new HolidayService(http));
    }

    [Theory]
    [InlineData("themes", "Themes")]
    [InlineData("holidays", "Holidays")]
    public async Task FailedLookupStillShowsTheEventsAndNamesTheList(string route, string name)
    {
        api.Status(HttpMethod.Get, $"/api/{route}", HttpStatusCode.InternalServerError);

        await Init();

        Assert.Equal(ev.Id, Assert.Single(Events).Id);
        Assert.False(Loads.Failed("Events"));
        Assert.True(Loads.Failed(name));
        Assert.Equal($"{name} could not be loaded. {ServerError}", Loads.Message);
        Assert.NotNull(Get("_games"));
        Assert.NotNull(Get("_activities"));
    }

    [Fact]
    public async Task BothLookupsFailingAreBothNamed()
    {
        api.Offline(HttpMethod.Get, "/api/themes").Offline(HttpMethod.Get, "/api/holidays");
        await Init();
        Assert.Single(Events);
        Assert.Equal($"Themes and Holidays could not be loaded. {Unreachable}", Loads.Message);
    }

    [Fact]
    public async Task FailedEventLoadShowsTheMessageAndRetryLoadsTheList()
    {
        api.Offline(HttpMethod.Get, "/api/events");

        await Init();

        Assert.Null(Get("_events"));
        Assert.True(Loads.Failed("Events"));
        Assert.Equal($"Events could not be loaded. {Unreachable}", Loads.Message);

        api.Json(HttpMethod.Get, "/api/events", new[] { ev });
        await (Task)Call("LoadAll")!;

        Assert.Null(Loads.Message);
        Assert.Single(Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSelectWinnerKeepsThePreviousWinnerAndShowsTheError(bool offline)
    {
        await Init();
        var listed = Assert.Single(Events);
        Call("ShowDetails", listed);
        var path = $"/api/events/{ev.Id}";
        if (offline) api.Offline(HttpMethod.Put, path);
        else api.Status(HttpMethod.Put, path, HttpStatusCode.InternalServerError);
        var reads = api.Count(HttpMethod.Get, "/api/events");

        await (Task)Call("SelectWinner", second.Id)!;

        var detail = (EventEntity)Get("_detailEvent")!;
        Assert.Same(listed, detail);
        Assert.Equal(first.Id, detail.WinnerActivityId);
        Assert.Equal(first.Id, Assert.Single(Events).WinnerActivityId);
        Assert.Equal(reads, api.Count(HttpMethod.Get, "/api/events"));
        Assert.Equal($"The winner was not saved. {(offline ? Unreachable : ServerError)}", Get("_winnerError"));
        Assert.Equal(1, api.Count(HttpMethod.Put, path));
    }

    [Fact]
    public async Task SelectWinnerSucceedsAfterAFailureAndClearsTheError()
    {
        await Init();
        Call("ShowDetails", Assert.Single(Events));
        api.Status(HttpMethod.Put, $"/api/events/{ev.Id}", HttpStatusCode.InternalServerError);
        await (Task)Call("SelectWinner", second.Id)!;
        Assert.NotNull(Get("_winnerError"));

        var saved = new EventEntity { Id = ev.Id, Name = ev.Name, Selections = ev.Selections, WinnerActivityId = second.Id };
        api.Json(HttpMethod.Put, $"/api/events/{ev.Id}", saved)
           .Json(HttpMethod.Get, "/api/events", new[] { saved });
        await (Task)Call("SelectWinner", second.Id)!;

        Assert.Null(Get("_winnerError"));
        Assert.Equal(second.Id, ((EventEntity)Get("_detailEvent")!).WinnerActivityId);
        Assert.Equal(second.Id, Assert.Single(Events).WinnerActivityId);
    }

    [Fact]
    public async Task FailedDeleteLeavesTheListAndShowsTheError()
    {
        await Init();
        api.Status(HttpMethod.Delete, $"/api/events/{ev.Id}", HttpStatusCode.InternalServerError);
        var list = Events;
        var reads = api.Count(HttpMethod.Get, "/api/events");

        await (Task)Call("Delete", ev.Id)!;

        Assert.Same(list, Events);
        Assert.Single(Events);
        Assert.Equal(reads, api.Count(HttpMethod.Get, "/api/events"));
        Assert.Equal($"The event was not deleted. {ServerError}", Get("_actionError"));
    }

    [Fact]
    public async Task FailedCommentSaveUsesTheSharedMessageAndKeepsTheText()
    {
        await Init();
        Call("ShowDetails", Assert.Single(Events));
        api.Status(HttpMethod.Patch, $"/api/activities/{first.Id}/comments", HttpStatusCode.InternalServerError);
        Call("SetDetailComment", first.Id, "Typed but not saved");

        await (Task)Call("SaveDetailComment", first.Id)!;

        Assert.Equal(ApiResult.CommentsNotSaved(ServerError), Get("_detailCommentError"));
        Assert.Equal("Typed but not saved", Call("GetDetailComment", first.Id));
        Assert.Equal(true, Call("IsDetailCommentDirty", first.Id));
    }

    [Fact]
    public async Task RejectedEventSaveShowsTheApisTextInTheDialog()
    {
        await Init();
        Call("ShowEditEvent", Assert.Single(Events));
        api.Status(HttpMethod.Put, $"/api/events/{ev.Id}", HttpStatusCode.BadRequest, "An activity may only be selected once.");

        await (Task)Call("SaveCustomizedEvent")!;

        Assert.Equal(true, Get("_showCustomize"));
        Assert.Equal("The event was not saved. An activity may only be selected once.", Get("_customizeError"));
        Assert.Equal("Game night", Get("_custName"));
    }

    private List<EventEntity> Events => (List<EventEntity>)Get("_events")!;
    private LoadErrors Loads => (LoadErrors)Get("_loads")!;
    private Task Init() => (Task)typeof(Events).GetMethod("OnInitializedAsync", Private)!.Invoke(page, null)!;
    private void Inject(string name, object service) => typeof(Events).GetProperty(name, Private)!.SetValue(page, service);
    private object? Get(string name) => typeof(Events).GetField(name, Private)!.GetValue(page);
    private object? Call(string name, params object[] args) => typeof(Events).GetMethod(name, Private)!.Invoke(page, args);
}
