using System.Text.Json;
using Microsoft.Playwright;

namespace MW_GC.EventManager.E2ETests;

/// <summary>
/// The API through the emulator, with the session's admin cookie: what a flow seeds, reads back and removes. Every
/// item a flow adds is deleted after the flow, and this class alone knows the API's delete rules.
/// </summary>
internal sealed class ApiSeedClient(IAPIRequestContext http, string appUrl, CleanupStack cleanups)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Adds a Game; cleanup deletes its Activities, then the Game.</summary>
    public Task<SeededItem> AddGameAsync(string name) => AddLibraryItemAsync("games", name);

    /// <summary>
    /// Adds a Game, Theme or Holiday by its route (<c>games</c>, <c>themes</c>, <c>holidays</c>). Cleanup deletes it;
    /// a delete of an item that is already gone answers 204 too, so this also covers an item the flow deleted.
    /// </summary>
    public async Task<SeededItem> AddLibraryItemAsync(string route, string name)
    {
        var item = await ReadAsync<SeededItem>(HttpMethod.Post, $"/api/{route}", new NameBody(name));
        cleanups.Push($"{route} {name}", route == "games"
            ? () => DeleteGameAndActivitiesAsync(item.Id)
            : () => DeleteAsync($"/api/{route}/{item.Id}"));
        return item;
    }

    /// <summary>Adds an Activity to a seeded Game; the Game's cleanup deletes it.</summary>
    public Task<SeededActivity> AddActivityAsync(SeededItem game, string name) =>
        ReadAsync<SeededActivity>(HttpMethod.Post, "/api/activities", new ActivityBody(game.Id, name, "d", "r"));

    /// <summary>Adds an Event; cleanup deletes it (a 204 also covers an Event the flow deleted).</summary>
    public async Task<SeededEvent> AddEventAsync(EventBody body)
    {
        var created = await ReadAsync<SeededEvent>(HttpMethod.Post, "/api/events", body);
        cleanups.Push($"Event {body.Name}", () => DeleteAsync($"/api/events/{created.Id}"));
        return created;
    }

    public Task<SeededEvent> GetEventAsync(Guid id) => ReadAsync<SeededEvent>(HttpMethod.Get, $"/api/events/{id}");

    public Task<List<SeededActivity>> ListActivitiesAsync() => ReadAsync<List<SeededActivity>>(HttpMethod.Get, "/api/activities");

    /// <summary>The status a GET of this path answers, for flows that check whether an item still exists.</summary>
    internal async Task<int> GetStatusAsync(string path) => (await http.GetAsync(appUrl + path)).Status;

    // Children first: a Game that still has Activities refuses its delete.
    private async Task DeleteGameAndActivitiesAsync(Guid gameId)
    {
        foreach (var activity in await ListActivitiesAsync())
        {
            if (activity.GameId == gameId) await DeleteAsync($"/api/activities/{activity.Id}");
        }
        await DeleteAsync($"/api/games/{gameId}");
    }

    private Task DeleteAsync(string path) => SendAsync(HttpMethod.Delete, path);

    private async Task<T> ReadAsync<T>(HttpMethod method, string path, object? body = null)
    {
        var response = await SendAsync(method, path, body);
        return JsonSerializer.Deserialize<T>(await response.TextAsync(), Json)
            ?? throw new AssertFailedException($"{method} {path} answered no item.");
    }

    // Fails unless the API answers with a success status.
    private async Task<IAPIResponse> SendAsync(HttpMethod method, string path, object? body = null)
    {
        var options = new APIRequestContextOptions { Method = method.Method };
        if (body is not null)
        {
            options.DataString = JsonSerializer.Serialize(body, body.GetType(), Json);
            options.Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" };
        }
        var response = await http.FetchAsync(appUrl + path, options);
        Assert.IsTrue(response.Ok, $"{method} {path} answered {response.Status}: {await response.TextAsync()}");
        return response;
    }
}
