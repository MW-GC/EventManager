using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Services;
using static MW_GC.EventManager.Web.Tests.SlotTestData;

namespace MW_GC.EventManager.Web.Tests;

// Exercise the actual page handlers without a renderer or network services. The slot rules
// themselves are tested on SlotPlanner (the classes on SlotPlannerFixture); these tests reach the page's
// planner through its public API and keep reflection only for the page's own handlers and fields, which
// since #93 live in the create-and-edit and details dialogs (UnrenderedEventsPage finds them).
// The tests on it were split by behaviour from SlotRerollTests (#268): SlotRerollTests, SlotSaveTests and
// SlotSaveFailureTests.
public abstract class SlotPageFixture
{
    // The page and its two dialogs (#93): the handlers and fields below moved into the dialogs.
    private protected readonly UnrenderedEventsPage page = new();
    protected readonly Guid gameA = Guid.NewGuid();
    protected readonly Guid gameB = Guid.NewGuid();
    protected readonly Guid theme = Guid.NewGuid();
    protected readonly Guid holiday = Guid.NewGuid();
    protected readonly ActivityEntity current;
    protected readonly ActivityEntity other;
    protected readonly ActivityEntity replacement;

    protected SlotPageFixture()
    {
        current = Activity(gameA);
        other = Activity(gameB);
        replacement = Activity(gameA);
        replacement.ThemeIds = [theme];
        replacement.HolidayIds = [holiday];
        Set("_games", new List<GameEntity> { new() { Id = gameA }, new() { Id = gameB } });
        Set("_activities", new List<ActivityEntity> { current, other, replacement });
        AddSlot(current);
        AddSlot(other);
    }

    private protected EventHttpHandler ConfigureSave()
    {
        Set("_custName", "Test event");
        Set("_showCustomize", true);
        var handler = new EventHttpHandler();
        var service = new MW_GC.EventManager.Web.Services.EventService(new HttpClient(handler) { BaseAddress = new Uri("https://example.test") });
        page.SetService("EventSvc", service);
        return handler;
    }

    protected Task Save() => (Task)page.Call("SaveCustomizedEvent")!;

    private protected sealed class EventHttpHandler : HttpMessageHandler
    {
        public bool Fail { get; set; }
        public bool LoseWriteResponse { get; set; }
        public bool Timeout { get; set; }
        public List<string?> CreateKeys { get; } = [];
        public Dictionary<Guid, EventEntity> Created { get; } = [];
        public bool NetworkFailure { get; set; }
        public bool FailRead { get; set; }
        public string? ReadJson { get; set; }
        public int Reads { get; private set; }
        public EventEntity? Saved { get; private set; }
        public HttpMethod? LastWrite { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                Reads++;
                if (FailRead) throw new HttpRequestException("Read unavailable");
                if (ReadJson is not null)
                    return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(ReadJson, System.Text.Encoding.UTF8, "application/json") };
                return new(System.Net.HttpStatusCode.OK) { Content = System.Net.Http.Json.JsonContent.Create(new[] { Saved! }) };
            }
            if (NetworkFailure) throw new HttpRequestException("Offline");
            LastWrite = request.Method;
            if (Fail) return new(System.Net.HttpStatusCode.BadRequest) { Content = new StringContent("Invalid activity") };
            Saved = System.Text.Json.JsonSerializer.Deserialize<EventEntity>(await request.Content!.ReadAsStringAsync(cancellationToken), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            if (request.Method == HttpMethod.Post)
            {
                var key = request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null;
                CreateKeys.Add(key);
                Saved!.Id = key is null ? Guid.NewGuid() : Guid.Parse(key);
                Created.TryAdd(Saved.Id, Saved);
                Saved = Created[Saved.Id];
            }
            else
                Assert.IsFalse(request.Headers.Contains("Idempotency-Key"));
            if (LoseWriteResponse)
            {
                if (Timeout) throw new TaskCanceledException("Response timed out after commit");
                throw new HttpRequestException("Response lost after commit");
            }
            return new(System.Net.HttpStatusCode.OK) { Content = System.Net.Http.Json.JsonContent.Create(Saved) };
        }
    }
    protected SlotPlanner Planner => (SlotPlanner)Get("_planner")!;
    protected IReadOnlyList<SlotSelection> Slots => Planner.Slots;
    protected List<ActivityEntity> Activities => (List<ActivityEntity>)Get("_activities")!;
    protected List<ActivityEntity> Candidates() => Planner.GetRerollCandidates(0);
    protected object? Get(string name) => page.Get(name);

    // Sets a page field, then hands the planner what the page would: the library after a load,
    // the filters after one of their inputs changes.
    protected void Set(string name, object value)
    {
        page.Set(name, value);
        if (name is "_games" or "_activities")
            Planner.SetLibrary((List<GameEntity>?)Get("_games"), (List<ActivityEntity>?)Get("_activities"));
        if (name is "_custThemedOnly" or "_custGameIds" or "_custThemeIds" or "_custHolidayIds" or "_custUniqueGames")
            Call("SyncFilter");
    }

    protected object? Call(string name, params object[] args) => page.Call(name, args);
    protected static Guid Id(object slot, string name) =>
        name == "GameId" ? ((SlotSelection)slot).GameId : ((SlotSelection)slot).ActivityId;
    protected void AddSlot(ActivityEntity activity) => Planner.SetSlots([.. Slots, Slot(activity)]);
    protected void SetSlots(params ActivityEntity[] activities) => Planner.SetSlots(activities.Select(Slot));

    // Gives the page a planner that rolls with random, keeping its library, filters, slots and errors.
    protected void UseRandom(Random random)
    {
        var planner = new SlotPlanner(random) { Filter = Planner.Filter, GenerationError = Planner.GenerationError, AddSlotError = Planner.AddSlotError };
        planner.SetLibrary((List<GameEntity>?)Get("_games"), (List<ActivityEntity>?)Get("_activities"));
        planner.SetSlots(Slots);
        page.Set("_planner", planner);
    }

    // Replaces the fixture library with one game per entry, holding that many activities each.
    protected List<GameEntity> UseLibrary(params int[] activitiesPerGame)
    {
        var games = new List<GameEntity>();
        var activities = new List<ActivityEntity>();
        foreach (var count in activitiesPerGame)
        {
            var game = new GameEntity { Id = Guid.NewGuid() };
            games.Add(game);
            for (var i = 0; i < count; i++) activities.Add(Activity(game.Id));
        }
        Set("_games", games);
        Set("_activities", activities);
        return games;
    }

    protected void AssertFilledSlots(int expected, bool distinctGames)
    {
        Assert.AreEqual(expected, Slots.Count);
        Assert.IsNull(Planner.GenerationError);
        var slots = Slots.Cast<object>().ToArray();
        Assert.AreEqual(expected, slots.Select(s => Id(s, "ActivityId")).Distinct().Count());
        if (distinctGames) Assert.AreEqual(expected, slots.Select(s => Id(s, "GameId")).Distinct().Count());
    }
}
