using System.Net;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Tests;

// The generic Library client and how the app registers it. Split out of LibraryPageTests (#196.10).
[TestClass]
public sealed class LibraryClientTests
{
    private static readonly Guid Id = Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");
    private static readonly Guid GameIdOfRace = Guid.Parse("22222222-3333-4444-5555-666666666666");
    private static readonly Guid SpookyId = Guid.Parse("33333333-4444-5555-6666-777777777777");

    // EntityBase's fields, which System.Text.Json writes after the entity's own.
    private const string BaseFields = "\"partitionKey\":\"\",\"rowKey\":\"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0\",\"timestamp\":null,\"eTag\":null,\"id\":\"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0\"}";

    // Each client the app registers (AddLibraryClients) sends what the service it replaced sent: the same verb,
    // path, headers (no If-Match) and body, call for call. GameService's writes, ThemeService and HolidayService were
    // removed in #125, ActivityService's read, create, update and delete in #126, and GameService's read in #196.1.
    // ActivityService's last call, the duplicate, moved onto the Activities' client in #200.1 (ServiceRouteContractTests).
    // The paths are written out per list and the bodies by hand (web-default JSON: camelCase, the entity's fields),
    // so a wrong route in the registration or a changed body fails here.
    [TestMethod]
    [DataRow("games")]
    [DataRow("themes")]
    [DataRow("holidays")]
    [DataRow("activities")]
    public async Task LibraryClient_SendsExactlyWhatTheServiceItReplacesSends(string list)
    {
        var recorder = new Recorder();
        await using var services = LibraryServices.Over(recorder.Client());
        switch (list)
        {
            case "games":
                await AssertRequests(services.GetRequiredService<LibraryClient<GameEntity>>(), recorder,
                    new GameEntity { Id = Id, Name = "Kart", Website = "https://example.test/kart" }, "/api/games",
                    "{\"name\":\"Kart\",\"imageUrl\":null,\"website\":\"https://example.test/kart\",\"iconUrl\":null," + BaseFields);
                break;
            case "themes":
                await AssertRequests(services.GetRequiredService<LibraryClient<ThemeEntity>>(), recorder,
                    new ThemeEntity { Id = Id, Name = "Spooky" }, "/api/themes",
                    "{\"name\":\"Spooky\"," + BaseFields);
                break;
            case "holidays":
                await AssertRequests(services.GetRequiredService<LibraryClient<HolidayEntity>>(), recorder,
                    new HolidayEntity { Id = Id, Name = "Halloween" }, "/api/holidays",
                    "{\"name\":\"Halloween\"," + BaseFields);
                break;
            default:
                await AssertRequests(services.GetRequiredService<LibraryClient<ActivityEntity>>(), recorder,
                    new ActivityEntity { Id = Id, Name = "Race", GameId = GameIdOfRace, ThemeIds = [SpookyId], MinPlayers = 2, MaxPlayers = 6, DurationMinutes = 45 },
                    "/api/activities",
                    "{\"gameId\":\"22222222-3333-4444-5555-666666666666\",\"name\":\"Race\",\"description\":\"\",\"rules\":\"\"," +
                    "\"themeIds\":[\"33333333-4444-5555-6666-777777777777\"],\"holidayIds\":[],\"setupRequirements\":\"\"," +
                    "\"minPlayers\":2,\"maxPlayers\":6,\"durationMinutes\":45," + BaseFields);
                break;
        }
    }

    // #196.4: Program.cs registers the app's services through AddAppServices and nothing else, so building that
    // here is building what the app builds: each of the four Library clients resolves over the app's HttpClient,
    // whose address is the API base Program.cs passes. Removing AddLibraryClients from it fails here.
    [TestMethod]
    public async Task AppServices_RegisterTheFourLibraryClients()
    {
        await using var services = new ServiceCollection()
            .AddSingleton<NavigationManager>(new FixedNavigation())
            .AddAppServices("https://api.example.test/")
            .BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();

        Assert.IsNotNull(scope.ServiceProvider.GetService<LibraryClient<GameEntity>>(), "Games");
        Assert.IsNotNull(scope.ServiceProvider.GetService<LibraryClient<ActivityEntity>>(), "Activities");
        Assert.IsNotNull(scope.ServiceProvider.GetService<LibraryClient<ThemeEntity>>(), "Themes");
        Assert.IsNotNull(scope.ServiceProvider.GetService<LibraryClient<HolidayEntity>>(), "Holidays");
        Assert.AreEqual(new Uri("https://api.example.test/"), scope.ServiceProvider.GetRequiredService<HttpClient>().BaseAddress);
    }

    private static async Task AssertRequests<T>(LibraryClient<T> client, Recorder recorder, T item, string path, string body) where T : EntityBase
    {
        await client.GetAllAsync();
        Assert.IsNull(await client.CreateAsync(item));
        Assert.IsNull(await client.UpdateAsync(item));
        Assert.IsNull(await client.DeleteAsync(Id));

        var one = $"{path}/0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0";
        CollectionAssert.AreEqual(new[] { $"GET {path} []", $"POST {path} []", $"PUT {one} []", $"DELETE {one} []" },
            recorder.Sent.Select(s => s.Request).ToList());
        CollectionAssert.AreEqual(new string?[] { null, body, body, null }, recorder.Sent.Select(s => s.Body).ToList());
    }

    // The NavigationManager the browser host gives the app; the session handler only reads it on a 401.
    private sealed class FixedNavigation : NavigationManager
    {
        public FixedNavigation() => Initialize("https://example.test/", "https://example.test/");
    }

    // Records each request as "METHOD path [header names]" and its body (null when it has none), and answers
    // 200 with JSON null.
    private sealed class Recorder : HttpMessageHandler
    {
        public List<(string Request, string? Body)> Sent { get; } = [];

        public HttpClient Client() => new(this) { BaseAddress = new Uri("https://example.test/") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = string.Join(",", request.Headers.Select(h => h.Key).Order());
            Sent.Add(($"{request.Method} {request.RequestUri!.PathAndQuery} [{headers}]", body));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null", Encoding.UTF8, "application/json") };
        }
    }
}
