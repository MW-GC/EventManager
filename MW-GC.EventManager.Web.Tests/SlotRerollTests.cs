using Bunit;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Tests;

// Exercise the actual page handlers without a renderer or network services. The slot rules
// themselves are tested on SlotPlanner (SlotPlannerTests); these tests reach the page's planner
// through its public API and keep reflection only for the page's own handlers and fields, which
// since #93 live in the create-and-edit and details dialogs (UnrenderedEventsPage finds them).
[TestClass]
public class SlotRerollTests
{
    // The page and its two dialogs (#93): the handlers and fields below moved into the dialogs.
    private readonly UnrenderedEventsPage page = new();
    private readonly Guid gameA = Guid.NewGuid();
    private readonly Guid gameB = Guid.NewGuid();
    private readonly Guid theme = Guid.NewGuid();
    private readonly Guid holiday = Guid.NewGuid();
    private readonly ActivityEntity current;
    private readonly ActivityEntity other;
    private readonly ActivityEntity replacement;

    public SlotRerollTests()
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

    [TestMethod]
    public void RerollChangesOnlyTargetAndPreservesMetadata()
    {
        Set("_custName", "Keep this name");
        var date = new DateTime(2026, 9, 21);
        Set("_custDate", date);
        var originalOther = Slots[1];
        Planner.RerollSlot(0);
        Assert.AreEqual(replacement.Id, Id(Slots[0]!, "ActivityId"));
        Assert.AreEqual(gameA, Id(Slots[0]!, "GameId"));
        Assert.AreSame(originalOther, Slots[1]);
        Assert.AreEqual(2, Slots.Count);
        Assert.AreEqual("Keep this name", Get("_custName"));
        Assert.AreEqual(date, Get("_custDate"));
    }

    // The planner rows of this test (RandomizeAll, RerollSlot, ...) are in SlotPlannerTests.
    [TestMethod]
    [DataRow("ShowEditEvent", true)]
    [DataRow("ShowCustomize", true)]
    public void SuccessfulSelectionMutationClearsStaleGenerationError(string handler, bool unique)
    {
        Set("_custUniqueGames", unique);
        UseRandom(new FirstChoiceRandom());
        Planner.GenerationError = "Previous generation failure";
        if (handler == "ShowCustomize")
        {
            var game = new GameEntity { Id = Guid.NewGuid() };
            ((List<GameEntity>)Get("_games")!).Add(game);
            Activities.Add(Activity(game.Id));
        }

        switch (handler)
        {
            case "ShowEditEvent":
                Call(handler, new EventEntity { Name = "Existing event", Selections = [] });
                Assert.IsEmpty(Slots);
                break;
            default:
                Call(handler);
                Assert.AreEqual(3, Slots.Count);
                break;
        }
        Assert.IsNull(Planner.GenerationError);
    }

    [TestMethod]
    public void FailedCustomizeGenerationRetainsError()
    {
        Activities.Clear();
        Planner.GenerationError = "Previous failure";
        Call("ShowCustomize");
        Assert.IsNotNull(Planner.GenerationError);
        Assert.IsEmpty(Slots);
        Assert.AreEqual(true, Get("_showCustomize"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedSaveKeepsGenerationErrorAndSelections(bool editing)
    {
        const string error = "Previous generation failure";
        Planner.GenerationError = error;
        Set("_custName", "Keep this name");
        Set("_showCustomize", true);
        if (editing) Set("_editingEvent", new EventEntity { Id = Guid.NewGuid() });
        using var http = new HttpClient(new FailedSaveHandler()) { BaseAddress = new Uri("https://example.invalid/") };
        page.SetService("EventSvc", new MW_GC.EventManager.Web.Services.EventService(http));
        var original = Slots;
        var originalSlots = Slots.Cast<object>().ToArray();

        await (Task)Call("SaveCustomizedEvent")!;

        Assert.IsNotNull(Get("_customizeError"));
        Assert.AreEqual(error, Planner.GenerationError);
        Assert.AreSame(original, Slots);
        Assert.AreSequenceEqual(originalSlots, Slots.Cast<object>().ToArray());
        Assert.AreEqual("Keep this name", Get("_custName"));
        Assert.AreEqual(true, Get("_showCustomize"));
        Assert.AreEqual(false, Get("_generating"));
    }

    private sealed class FailedSaveHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(new HttpRequestException("Save failed"));
    }

    // Stable sort keys and first-choice draws force the greedy dead end.
    private sealed class FirstChoiceRandom : Random
    {
        public override int Next() => 0;
        public override int Next(int maxValue) => 0;
    }

    private static ActivityEntity Activity(Guid game) => new() { Id = Guid.NewGuid(), GameId = game };

    [TestMethod]
    public async Task OneSlotCreateAndEditSendAutomaticWinnerAndReload()
    {
        Planner.RemoveSlot(1);
        Set("_custName", "One activity");
        var handler = new EventHttpHandler();
        var service = new MW_GC.EventManager.Web.Services.EventService(new HttpClient(handler) { BaseAddress = new Uri("https://example.test") });
        page.SetService("EventSvc", service);
        await (Task)page.Call("SaveCustomizedEvent")!;
        Assert.AreEqual(HttpMethod.Post, handler.LastWrite);
        Assert.AreEqual(current.Id, handler.Saved!.WinnerActivityId);
        Assert.AreEqual(current.Id, Assert.ContainsSingle((List<EventEntity>)Get("_events")!).WinnerActivityId);

        page.Call("ShowEditEvent", handler.Saved);
        Assert.ContainsSingle(Slots.Cast<object>());
        Set("_custGameIds", new List<Guid> { gameA });
        Assert.AreEqual(replacement.Id, Assert.ContainsSingle(Candidates()).Id);
        Planner.RerollSlot(0);
        var replacementId = Id(Slots[0]!, "ActivityId");
        await (Task)page.Call("SaveCustomizedEvent")!;
        Assert.AreEqual(HttpMethod.Put, handler.LastWrite);
        Assert.AreEqual(replacementId, handler.Saved!.WinnerActivityId);
        Assert.AreEqual("One activity", handler.Saved.Name);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(6)]
    public async Task InvalidSlotCountDoesNotSendRequest(int count)
    {
        Planner.SetSlots([]);
        // Every slot is otherwise valid, isolating the count guard from duplicate guards.
        Set("_custUniqueGames", false);
        for (var i = 0; i < count; i++)
        {
            var activity = Activity(gameA);
            Activities.Add(activity);
            AddSlot(activity);
        }
        var handler = ConfigureSave();
        await Save();
        Assert.IsNull(handler.LastWrite);
        Assert.Contains("Select 1–5", (string)Get("_customizeError")!);
        Assert.AreEqual(true, Get("_showCustomize"));
        Assert.AreEqual(false, Get("_generating"));
    }

    [TestMethod]
    public async Task IncompleteSlotIsNotSilentlyDroppedIntoAnAutomaticWinner()
    {
        Slots[1].ActivityId = Guid.Empty;
        var handler = ConfigureSave();
        await Save();
        Assert.IsNull(handler.LastWrite);
        Assert.Contains("every slot", (string)Get("_customizeError")!);
        Assert.AreEqual(2, Slots.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SaveFailureKeepsDialogAndSelectionsForRetry(bool networkFailure)
    {
        Planner.RemoveSlot(1);
        var handler = ConfigureSave();
        handler.Fail = true;
        handler.NetworkFailure = networkFailure;
        await Save();
        Assert.AreEqual(true, Get("_showCustomize"));
        Assert.AreEqual(false, Get("_generating"));
        Assert.IsNotNull(Get("_customizeError"));
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(current.Id, Id(Slots[0]!, "ActivityId"));
        handler.Fail = false;
        handler.NetworkFailure = false;
        await Save();
        Assert.IsNull(Get("_customizeError"));
        Assert.AreEqual(false, Get("_showCustomize"));
        Assert.AreEqual(1, handler.Reads);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MultiSelectionEditPreservesOnlyAnExistingWinner(bool replaceWinner)
    {
        var handler = ConfigureSave();
        await Save();
        Assert.IsNull(handler.Saved!.WinnerActivityId);
        handler.Saved.WinnerActivityId = current.Id;
        page.Call("ShowEditEvent", handler.Saved);
        if (replaceWinner) Planner.RerollSlot(0);
        await Save();
        Assert.AreEqual(HttpMethod.Put, handler.LastWrite);
        Assert.AreEqual(2, handler.Saved!.Selections.Count);
        Assert.AreEqual(replaceWinner ? (Guid?)null : current.Id, handler.Saved.WinnerActivityId);
    }

    [TestMethod]
    public async Task SavedSoleWinnerIsReadBackForListAndDetails()
    {
        Planner.RemoveSlot(1);
        var handler = ConfigureSave();
        await Save();
        var persisted = Assert.ContainsSingle((List<EventEntity>)Get("_events")!);
        Assert.AreNotSame(handler.Saved, persisted);
        page.Call("ShowDetails", persisted);
        var detail = (EventEntity)Get("_detailEvent")!;
        Assert.AreEqual(Assert.ContainsSingle(detail.Selections).Activity.Id, detail.WinnerActivityId);
        Assert.AreSame(persisted, detail);
        Assert.AreEqual(1, handler.Reads);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("{")]
    [DataRow("[{\"id\":\"not-a-guid\"}]")]
    public async Task SuccessfulSaveWithFailedReadBackDoesNotOfferCreateRetry(string? readJson)
    {
        Planner.RemoveSlot(1);
        var handler = ConfigureSave();
        handler.FailRead = readJson is null;
        handler.ReadJson = readJson;
        await Save();
        Assert.AreEqual(current.Id, handler.Saved!.WinnerActivityId);
        Assert.AreEqual(false, Get("_showCustomize"));
        Assert.IsNull(Get("_customizeError"));
        Assert.Contains("event was saved", (string)Get("_pageError")!);
        Assert.AreEqual(false, Get("_generating"));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task SameGameSelectionsRequireUniqueGamesToBeDisabled(bool unique)
    {
        Planner.SetSlots([]);
        AddSlot(current);
        AddSlot(replacement);
        Set("_custUniqueGames", unique);
        var handler = ConfigureSave();
        await Save();
        if (unique)
        {
            Assert.IsNull(handler.Saved);
            Assert.Contains("distinct", (string)Get("_customizeError")!);
        }
        else
        {
            Assert.AreEqual(2, handler.Saved!.Selections.Count);
            Assert.IsFalse(handler.Saved.UniqueGamesOnly);
            Assert.IsNull(handler.Saved.WinnerActivityId);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task DuplicateActivityIsRejectedEvenWhenRepeatedGamesAreAllowed(bool unique)
    {
        Planner.SetSlots([]);
        AddSlot(current);
        AddSlot(current);
        Set("_custUniqueGames", unique);
        var handler = ConfigureSave();
        await Save();
        Assert.IsNull(handler.LastWrite);
        Assert.IsNull(handler.Saved);
        Assert.AreEqual(0, handler.Reads);
        Assert.Contains("distinct", (string)Get("_customizeError")!);
        Assert.AreEqual(true, Get("_showCustomize"));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task MultiSlotEditReducedToOneReloadsAutomaticWinner(bool hadWinner)
    {
        var handler = ConfigureSave();
        await Save();
        Assert.IsNotNull(handler.Saved);
        handler.Saved.WinnerActivityId = hadWinner ? other.Id : null;
        page.Call("ShowEditEvent", handler.Saved);
        Planner.RemoveSlot(1);
        await Save();
        Assert.IsNull(Get("_customizeError"));
        Assert.AreEqual(HttpMethod.Put, handler.LastWrite);
        Assert.AreEqual(current.Id, Assert.ContainsSingle(handler.Saved.Selections).Activity.Id);
        Assert.AreEqual(current.Id, handler.Saved.WinnerActivityId);
        var listed = Assert.ContainsSingle((List<EventEntity>)Get("_events")!);
        page.Call("ShowDetails", listed);
        Assert.AreEqual(current.Id, ((EventEntity)Get("_detailEvent")!).WinnerActivityId);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AmbiguousCreateRetryReusesKeyAndNewDialogGetsNewKey(bool timeout)
    {
        Planner.RemoveSlot(1);
        var handler = ConfigureSave();
        handler.LoseWriteResponse = true;
        handler.Timeout = timeout;
        await Save();
        Assert.AreEqual(true, Get("_showCustomize"));
        Assert.IsNotNull(Get("_customizeError"));
        var key = Assert.ContainsSingle(handler.CreateKeys);
        Assert.IsTrue(Guid.TryParse(key, out var id));
        Assert.AreNotEqual(Guid.Empty, id);
        var committedId = handler.Saved!.Id;

        handler.LoseWriteResponse = false;
        await Save();
        Assert.AreSequenceEqual(new[] { key, key }, handler.CreateKeys);
        Assert.AreEqual(committedId, handler.Saved.Id);
        Assert.ContainsSingle(handler.Created);
        Assert.AreEqual(false, Get("_showCustomize"));
        Assert.IsNull(Get("_customizeError"));
        Assert.AreEqual(committedId, Assert.ContainsSingle((List<EventEntity>)Get("_events")!).Id);

        page.Call("ShowCustomize");
        // ShowCustomize fills as many slots as the two-game fixture allows (here two);
        // pin the single-slot state this test saves instead of relying on that draw.
        SetSlots(current);
        Set("_custName", "Another event");
        await Save();
        Assert.AreNotEqual(key, handler.CreateKeys[2]);
        Assert.AreEqual(2, handler.Created.Count);
    }

    private EventHttpHandler ConfigureSave()
    {
        Set("_custName", "Test event");
        Set("_showCustomize", true);
        var handler = new EventHttpHandler();
        var service = new MW_GC.EventManager.Web.Services.EventService(new HttpClient(handler) { BaseAddress = new Uri("https://example.test") });
        page.SetService("EventSvc", service);
        return handler;
    }

    private Task Save() => (Task)page.Call("SaveCustomizedEvent")!;

    private sealed class EventHttpHandler : HttpMessageHandler
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
    private SlotPlanner Planner => (SlotPlanner)Get("_planner")!;
    private IReadOnlyList<SlotSelection> Slots => Planner.Slots;
    private List<ActivityEntity> Activities => (List<ActivityEntity>)Get("_activities")!;
    private List<ActivityEntity> Candidates() => Planner.GetRerollCandidates(0);
    private object? Get(string name) => page.Get(name);

    // Sets a page field, then hands the planner what the page would: the library after a load,
    // the filters after one of their inputs changes.
    private void Set(string name, object value)
    {
        page.Set(name, value);
        if (name is "_games" or "_activities")
            Planner.SetLibrary((List<GameEntity>?)Get("_games"), (List<ActivityEntity>?)Get("_activities"));
        if (name is "_custThemedOnly" or "_custGameIds" or "_custThemeIds" or "_custHolidayIds" or "_custUniqueGames")
            Call("SyncFilter");
    }

    private object? Call(string name, params object[] args) => page.Call(name, args);
    private static Guid Id(object slot, string name) =>
        name == "GameId" ? ((SlotSelection)slot).GameId : ((SlotSelection)slot).ActivityId;
    private static SlotSelection Slot(ActivityEntity activity) => new() { GameId = activity.GameId, ActivityId = activity.Id };
    private void AddSlot(ActivityEntity activity) => Planner.SetSlots([.. Slots, Slot(activity)]);
    private void SetSlots(params ActivityEntity[] activities) => Planner.SetSlots(activities.Select(Slot));

    // Gives the page a planner that rolls with random, keeping its library, filters, slots and errors.
    private void UseRandom(Random random)
    {
        var planner = new SlotPlanner(random) { Filter = Planner.Filter, GenerationError = Planner.GenerationError, AddSlotError = Planner.AddSlotError };
        planner.SetLibrary((List<GameEntity>?)Get("_games"), (List<ActivityEntity>?)Get("_activities"));
        planner.SetSlots(Slots);
        page.Set("_planner", planner);
    }

    private const string NoMatchMessage = "No activities match the current filters. Adjust the filters or add activities to the library.";

    // Replaces the fixture library with one game per entry, holding that many activities each.
    private List<GameEntity> UseLibrary(params int[] activitiesPerGame)
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

    private void AssertFilledSlots(int expected, bool distinctGames)
    {
        Assert.AreEqual(expected, Slots.Count);
        Assert.IsNull(Planner.GenerationError);
        var slots = Slots.Cast<object>().ToArray();
        Assert.AreEqual(expected, slots.Select(s => Id(s, "ActivityId")).Distinct().Count());
        if (distinctGames) Assert.AreEqual(expected, slots.Select(s => Id(s, "GameId")).Distinct().Count());
    }

    [TestMethod]
    [DataRow(1, 1, 1)]
    [DataRow(2, 1, 2)]
    [DataRow(2, 2, 2)]
    [DataRow(1, 2, 1)]
    [DataRow(3, 1, 3)]
    [DataRow(5, 1, 3)]
    [DataRow(8, 2, 3)]
    public void FreshCustomizeSizesUniqueSlotsToTheGamesTheLibraryCanFill(int games, int activitiesPerGame, int expectedSlots)
    {
        UseLibrary(Enumerable.Repeat(activitiesPerGame, games).ToArray());
        Planner.GenerationError = "Previous failure";
        Call("ShowCustomize");
        AssertFilledSlots(expectedSlots, distinctGames: true);
        Assert.AreEqual(true, Get("_showCustomize"));
    }

    // Rendered with bUnit and driven by a click: two Games and no Activities in the library.
    [TestMethod]
    public void FreshCustomizeWithNoActivitiesReportsNoMatchRatherThanKeptSelections()
    {
        var api = new StubApiHandler()
            .Json(HttpMethod.Get, "/api/games", new[] { new GameEntity { Name = "Kart" }, new GameEntity { Name = "Brawl" } })
            .Json(HttpMethod.Get, "/api/activities", Array.Empty<ActivityEntity>())
            .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
            .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>())
            .Json(HttpMethod.Get, "/api/events", new[] { new EventEntity { Name = "Game night" } });
        using var harness = new PageHarness(api);
        var rendered = harness.Render<Events>();
        rendered.WaitForRows(1);

        rendered.Button("Create Event").Click();

        Assert.IsTrue(rendered.HasFormDialog());
        var dialog = rendered.FormDialog();
        Assert.IsEmpty(dialog.QuerySelectorAll("fluent-button[aria-label^='Re-roll slot']"));
        Assert.Contains("Game & Activity Selections (0)", dialog.TextContent);
        Assert.AreEqual(NoMatchMessage, Assert.ContainsSingle(dialog.Alerts()));
    }

    private const string AddSlotNoMatchReason = "No unused activity matches the current filters. Adjust the filters or add activities to the library.";

    private string? AddSlotBlockedReason() => Planner.GetAddSlotBlockedReason();
    private List<ActivityEntity> AddSlotCandidates() => Planner.GetAddSlotCandidates();

    // A blocked Add Activity Slot keeps every slot, keeps the generation error and says why.
    private void AssertAddSlotRefused(string expectedReason)
    {
        const string error = "Previous generation failure";
        Planner.GenerationError = error;
        var original = Slots;
        var originalSlots = Slots.Cast<object>().ToArray();
        Assert.AreEqual(expectedReason, AddSlotBlockedReason());

        Planner.AddSlot();

        Assert.AreSame(original, Slots);
        Assert.AreSequenceEqual(originalSlots, Slots.Cast<object>().ToArray());
        Assert.AreEqual(expectedReason, Planner.AddSlotError);
        Assert.AreEqual(error, Planner.GenerationError);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void AddSlotWithEveryActivityInASlotIsDisabledWithReason(bool unique)
    {
        // Three Games of one Activity each: Create Event fills all three and nothing is left.
        UseLibrary(1, 1, 1);
        Call("ShowCustomize");
        AssertFilledSlots(3, distinctGames: true);
        Set("_custUniqueGames", unique);
        Assert.IsEmpty(AddSlotCandidates());
        AssertAddSlotRefused(AddSlotNoMatchReason);
    }

    // Scenarios carried over from the API generator tests that #48 removed: the page is now the
    // only place that draws slots, so it must keep these rules.

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RepeatedGameIdsAreToleratedFirstGameWins(bool unique)
    {
        var first = new GameEntity { Id = Guid.NewGuid(), Name = "First" };
        var impostor = new GameEntity { Id = first.Id, Name = "Same id, listed later" };
        var activity = Activity(first.Id);
        Set("_games", new List<GameEntity> { first, impostor });
        Set("_activities", new List<ActivityEntity> { activity });
        Set("_custUniqueGames", unique);
        SetSlots(current);

        Planner.RandomizeAll();

        AssertFilledSlots(1, distinctGames: true);
        Assert.AreEqual(activity.Id, Id(Slots[0]!, "ActivityId"));
        Assert.AreEqual(first.Id, Id(Slots[0]!, "GameId"));
        // The saved snapshot takes the first Game listed with that id.
        var handler = ConfigureSave();
        await Save();
        var selection = Assert.ContainsSingle(handler.Saved!.Selections);
        Assert.AreEqual("First", selection.Game.Name);
        Assert.AreEqual(activity.Id, selection.Activity.Id);
    }

    // Event details: the Winner's comment is stored on the Activity.
    private (EventEntity Event, CommentHttpHandler Handler) OpenWinnerDetails(string? stored)
    {
        current.Name = "Winner activity";
        current.Comments = stored;
        var game = new MW_GC.EventManager.Shared.Models.Game { Id = gameA, Name = "Game A" };
        var snapshot = new MW_GC.EventManager.Shared.Models.Activity { Id = current.Id, GameId = gameA, Name = current.Name, Comments = stored };
        var otherSnapshot = new MW_GC.EventManager.Shared.Models.Activity { Id = other.Id, GameId = gameB, Name = "Other" };
        var ev = new EventEntity
        {
            Id = Guid.NewGuid(),
            Name = "Game night",
            Selections =
            [
                new() { Game = game, Activity = snapshot },
                new() { Game = new MW_GC.EventManager.Shared.Models.Game { Id = gameB, Name = "Game B" }, Activity = otherSnapshot }
            ],
            WinnerActivityId = current.Id
        };
        Set("_events", new List<EventEntity> { ev });
        var handler = new CommentHttpHandler();
        page.SetService("ActivitySvc",
            new MW_GC.EventManager.Web.Services.ActivityService(new HttpClient(handler) { BaseAddress = new Uri("https://example.test") }));
        Call("ShowDetails", ev);
        return (ev, handler);
    }

    private string DetailComment(Guid id) => (string)Call("GetDetailComment", id)!;
    private bool DetailCommentDirty(Guid id) => (bool)Call("IsDetailCommentDirty", id)!;
    private Task SaveComment(Guid id) => (Task)Call("SaveDetailComment", id)!;

    [TestMethod]
    [DataRow(null)]
    [DataRow("Old note")]
    public async Task SavedCommentStaysVisibleAfterSaveAndReopen(string? stored)
    {
        var (ev, handler) = OpenWinnerDetails(stored);
        Assert.AreEqual(stored ?? string.Empty, DetailComment(current.Id));
        Call("SetDetailComment", current.Id, "Ran long; start earlier");
        Assert.IsTrue(DetailCommentDirty(current.Id));

        await SaveComment(current.Id);

        Assert.AreEqual(HttpMethod.Patch, handler.LastMethod);
        Assert.AreEqual($"/api/activities/{current.Id}/comments", handler.LastPath);
        Assert.Contains("Ran long; start earlier", handler.LastBody);
        Assert.IsNull(Get("_detailCommentError"));
        Assert.AreEqual("Ran long; start earlier", DetailComment(current.Id));
        Assert.IsFalse(DetailCommentDirty(current.Id));
        // Both in-memory copies carry the saved text: the Activity list and the Event's snapshot.
        Assert.AreEqual("Ran long; start earlier", current.Comments);
        Assert.AreEqual("Ran long; start earlier", ev.Selections[0].Activity.Comments);
        Assert.IsNull(ev.Selections[1].Activity.Comments);

        Set("_detailEvent", null!);
        Call("ShowDetails", Assert.ContainsSingle((List<EventEntity>)Get("_events")!));
        Assert.AreEqual("Ran long; start earlier", DetailComment(current.Id));
        Assert.IsFalse(DetailCommentDirty(current.Id));

        // A list reload returns the Event as stored, whose snapshot still holds the old text;
        // the box follows the Activity, where the comment is actually saved.
        var reloaded = new EventEntity
        {
            Id = ev.Id,
            Name = ev.Name,
            Selections = [ev.Selections[0] with { Activity = ev.Selections[0].Activity with { Comments = stored } }, ev.Selections[1]],
            WinnerActivityId = current.Id
        };
        Call("ShowDetails", reloaded);
        Assert.AreEqual("Ran long; start earlier", DetailComment(current.Id));
        Assert.IsFalse(DetailCommentDirty(current.Id));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedCommentSaveKeepsTypedTextAndShowsError(bool networkFailure)
    {
        var (ev, handler) = OpenWinnerDetails("Old note");
        handler.Fail = true;
        handler.NetworkFailure = networkFailure;
        Call("SetDetailComment", current.Id, "Typed but not saved");

        await SaveComment(current.Id);

        Assert.IsFalse(string.IsNullOrWhiteSpace((string?)Get("_detailCommentError")));
        Assert.AreEqual("Typed but not saved", DetailComment(current.Id));
        Assert.IsTrue(DetailCommentDirty(current.Id));
        Assert.AreEqual("Old note", current.Comments);
        Assert.AreEqual("Old note", ev.Selections[0].Activity.Comments);

        // A retry that succeeds clears the error and keeps the text.
        handler.Fail = false;
        handler.NetworkFailure = false;
        await SaveComment(current.Id);
        Assert.IsNull(Get("_detailCommentError"));
        Assert.AreEqual("Typed but not saved", DetailComment(current.Id));
        Assert.IsFalse(DetailCommentDirty(current.Id));
        Assert.AreEqual("Typed but not saved", ev.Selections[0].Activity.Comments);
    }

    private sealed class CommentHttpHandler : HttpMessageHandler
    {
        public bool Fail { get; set; }
        public bool NetworkFailure { get; set; }
        public HttpMethod? LastMethod { get; private set; }
        public string? LastPath { get; private set; }
        public string LastBody { get; private set; } = string.Empty;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (NetworkFailure) throw new HttpRequestException("Offline");
            LastMethod = request.Method;
            LastPath = request.RequestUri!.AbsolutePath;
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(Fail ? System.Net.HttpStatusCode.InternalServerError : System.Net.HttpStatusCode.OK);
        }
    }
}
