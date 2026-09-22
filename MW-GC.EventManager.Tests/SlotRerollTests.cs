using System.Collections;
using System.Reflection;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using Xunit;

namespace MW_GC.EventManager.Tests;

// Exercise the actual page handlers without a renderer or network services.
public class SlotRerollTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Events page = new();
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

    [Fact]
    public void RerollChangesOnlyTargetAndPreservesMetadata()
    {
        Set("_custName", "Keep this name");
        var date = new DateTime(2026, 9, 21);
        Set("_custDate", date);
        var originalOther = Slots[1];
        Call("RerollSlot", 0);
        Assert.Equal(replacement.Id, Id(Slots[0]!, "ActivityId"));
        Assert.Equal(gameA, Id(Slots[0]!, "GameId"));
        Assert.Same(originalOther, Slots[1]);
        Assert.Equal(2, Slots.Count);
        Assert.Equal("Keep this name", Get("_custName"));
        Assert.Equal(date, Get("_custDate"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NeverDuplicatesActivitiesOrReturnsCurrent(bool unique)
    {
        Set("_custUniqueGames", unique);
        Assert.Equal(new[] { replacement.Id }, Candidates().Select(a => a.Id));
    }

    [Fact]
    public void UniqueGamesExcludesGamesUsedByOtherSlots()
    {
        var alternative = Activity(gameB);
        Activities.Add(alternative);
        Assert.DoesNotContain(alternative, Candidates());
        Set("_custUniqueGames", false);
        Assert.Contains(alternative, Candidates());
    }

    [Theory]
    [InlineData("_custGameIds")]
    [InlineData("_custThemeIds")]
    [InlineData("_custHolidayIds")]
    public void SelectedFiltersAreApplied(string field)
    {
        Set(field, new List<Guid> { field == "_custGameIds" ? gameA : field == "_custThemeIds" ? theme : holiday });
        Assert.Single(Candidates());
        Set(field, new List<Guid> { Guid.NewGuid() });
        Assert.Empty(Candidates());
        var original = Slots[0];
        Call("RerollSlot", 0);
        Assert.Same(original, Slots[0]);
    }

    [Fact]
    public void ThemedOnlyAcceptsThemeOrHolidayAndRejectsUntagged()
    {
        var untagged = Activity(gameA);
        Activities.Add(untagged);
        Set("_custThemedOnly", true);
        Assert.DoesNotContain(untagged, Candidates());
        replacement.ThemeIds.Clear();
        Assert.Contains(replacement, Candidates());
        replacement.HolidayIds.Clear();
        Assert.Empty(Candidates());
    }

    [Fact]
    public void FiltersCombineRatherThanOverrideEachOther()
    {
        Set("_custGameIds", new List<Guid> { gameA });
        Set("_custThemeIds", new List<Guid> { theme });
        Set("_custHolidayIds", new List<Guid> { holiday });
        Assert.Single(Candidates());
        replacement.HolidayIds.Clear();
        Assert.Empty(Candidates());
    }

    [Fact]
    public void ExhaustedPoolIsNoOpAndIgnoresOrphanedActivities()
    {
        Activities.Remove(replacement);
        Activities.Add(Activity(Guid.NewGuid()));
        var original = Slots[0];
        Assert.Empty(Candidates());
        Call("RerollSlot", 0);
        Assert.Same(original, Slots[0]);
        Assert.Equal(2, Slots.Count);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void InvalidIndexIsNoOp(int index)
    {
        Assert.Empty((List<ActivityEntity>)Call("GetRerollCandidates", index)!);
        Call("RerollSlot", index);
        Assert.Equal(current.Id, Id(Slots[0]!, "ActivityId"));
    }

    [Fact]
    public void RepeatedRollsKeepOtherSlotsAndStayInEligiblePool()
    {
        Activities.Add(Activity(gameA));
        var originalOther = Slots[1];
        for (var i = 0; i < 100; i++)
        {
            var before = Id(Slots[0]!, "ActivityId");
            var eligible = Candidates().Select(a => a.Id).ToList();
            Call("RerollSlot", 0);
            var after = Id(Slots[0]!, "ActivityId");
            Assert.NotEqual(before, after);
            Assert.Contains(after, eligible);
            Assert.Same(originalOther, Slots[1]);
        }
    }

    private static ActivityEntity Activity(Guid game) => new() { Id = Guid.NewGuid(), GameId = game };

    [Fact]
    public void RemoveAllowsOneSlotButNeverRemovesLastSlot()
    {
        Call("RemoveSlot", 1);
        Assert.Single(Slots.Cast<object>());
        Call("RemoveSlot", 0);
        Assert.Single(Slots.Cast<object>());
        Assert.Equal(current.Id, Id(Slots[0]!, "ActivityId"));
    }

    [Fact]
    public async Task OneSlotCreateAndEditSendAutomaticWinnerAndReload()
    {
        Call("RemoveSlot", 1);
        Set("_custName", "One activity");
        var handler = new EventHttpHandler();
        var service = new MW_GC.EventManager.Web.Services.EventService(new HttpClient(handler) { BaseAddress = new Uri("https://example.test") });
        typeof(Events).GetProperty("EventSvc", Private)!.SetValue(page, service);
        await (Task)typeof(Events).GetMethod("SaveCustomizedEvent", Private)!.Invoke(page, null)!;
        Assert.Equal(HttpMethod.Post, handler.LastWrite);
        Assert.Equal(current.Id, handler.Saved!.WinnerActivityId);
        Assert.Equal(current.Id, Assert.Single((List<EventEntity>)Get("_events")!).WinnerActivityId);

        typeof(Events).GetMethod("ShowEditEvent", Private)!.Invoke(page, [handler.Saved]);
        Assert.Single(Slots.Cast<object>());
        Set("_custGameIds", new List<Guid> { gameA });
        Assert.Equal(replacement.Id, Assert.Single(Candidates()).Id);
        Call("RerollSlot", 0);
        var replacementId = Id(Slots[0]!, "ActivityId");
        await (Task)typeof(Events).GetMethod("SaveCustomizedEvent", Private)!.Invoke(page, null)!;
        Assert.Equal(HttpMethod.Put, handler.LastWrite);
        Assert.Equal(replacementId, handler.Saved!.WinnerActivityId);
        Assert.Equal("One activity", handler.Saved.Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OneSlotRandomizationAndRerollRespectFilters(bool unique)
    {
        Call("RemoveSlot", 1);
        Set("_custUniqueGames", unique);
        Set("_custGameIds", new List<Guid> { gameA });
        Set("_custThemeIds", new List<Guid> { theme });
        Set("_custHolidayIds", new List<Guid> { holiday });
        Set("_custThemedOnly", true);
        typeof(Events).GetMethod("RandomizeAll", Private)!.Invoke(page, null);
        Assert.Single(Slots.Cast<object>());
        Assert.Equal(replacement.Id, Id(Slots[0]!, "ActivityId"));
        Assert.Empty(Candidates());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task InvalidSlotCountDoesNotSendRequest(int count)
    {
        Slots.Clear();
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
        Assert.Null(handler.LastWrite);
        Assert.Contains("Select 1–5", (string)Get("_customizeError")!);
        Assert.Equal(true, Get("_showCustomize"));
        Assert.Equal(false, Get("_generating"));
    }

    [Fact]
    public async Task IncompleteSlotIsNotSilentlyDroppedIntoAnAutomaticWinner()
    {
        Slots[1]!.GetType().GetProperty("ActivityId")!.SetValue(Slots[1], Guid.Empty);
        var handler = ConfigureSave();
        await Save();
        Assert.Null(handler.LastWrite);
        Assert.Contains("every slot", (string)Get("_customizeError")!);
        Assert.Equal(2, Slots.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveFailureKeepsDialogAndSelectionsForRetry(bool networkFailure)
    {
        Call("RemoveSlot", 1);
        var handler = ConfigureSave();
        handler.Fail = true;
        handler.NetworkFailure = networkFailure;
        await Save();
        Assert.Equal(true, Get("_showCustomize"));
        Assert.Equal(false, Get("_generating"));
        Assert.NotNull(Get("_customizeError"));
        Assert.Equal(0, handler.Reads);
        Assert.Equal(current.Id, Id(Slots[0]!, "ActivityId"));
        handler.Fail = false;
        handler.NetworkFailure = false;
        await Save();
        Assert.Null(Get("_customizeError"));
        Assert.Equal(false, Get("_showCustomize"));
        Assert.Equal(1, handler.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultiSelectionEditPreservesOnlyAnExistingWinner(bool replaceWinner)
    {
        var handler = ConfigureSave();
        await Save();
        Assert.Null(handler.Saved!.WinnerActivityId);
        handler.Saved.WinnerActivityId = current.Id;
        typeof(Events).GetMethod("ShowEditEvent", Private)!.Invoke(page, [handler.Saved]);
        if (replaceWinner) Call("RerollSlot", 0);
        await Save();
        Assert.Equal(HttpMethod.Put, handler.LastWrite);
        Assert.Equal(2, handler.Saved!.Selections.Count);
        Assert.Equal(replaceWinner ? (Guid?)null : current.Id, handler.Saved.WinnerActivityId);
    }

    [Fact]
    public async Task SavedSoleWinnerIsReadBackForListAndDetails()
    {
        Call("RemoveSlot", 1);
        var handler = ConfigureSave();
        await Save();
        var persisted = Assert.Single((List<EventEntity>)Get("_events")!);
        Assert.NotSame(handler.Saved, persisted);
        typeof(Events).GetMethod("ShowDetails", Private)!.Invoke(page, [persisted]);
        var detail = (EventEntity)Get("_detailEvent")!;
        Assert.Equal(Assert.Single(detail.Selections).Activity.Id, detail.WinnerActivityId);
        Assert.Same(persisted, detail);
        Assert.Equal(1, handler.Reads);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("[{\"id\":\"not-a-guid\"}]")]
    public async Task SuccessfulSaveWithFailedReadBackDoesNotOfferCreateRetry(string? readJson)
    {
        Call("RemoveSlot", 1);
        var handler = ConfigureSave();
        handler.FailRead = readJson is null;
        handler.ReadJson = readJson;
        await Save();
        Assert.Equal(current.Id, handler.Saved!.WinnerActivityId);
        Assert.Equal(false, Get("_showCustomize"));
        Assert.Null(Get("_customizeError"));
        Assert.Contains("event was saved", (string)Get("_pageError")!);
        Assert.Equal(false, Get("_generating"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SameGameSelectionsRequireUniqueGamesToBeDisabled(bool unique)
    {
        Slots.Clear();
        AddSlot(current);
        AddSlot(replacement);
        Set("_custUniqueGames", unique);
        var handler = ConfigureSave();
        await Save();
        if (unique)
        {
            Assert.Null(handler.Saved);
            Assert.Contains("distinct", (string)Get("_customizeError")!);
        }
        else
        {
            Assert.Equal(2, handler.Saved!.Selections.Count);
            Assert.False(handler.Saved.UniqueGamesOnly);
            Assert.Null(handler.Saved.WinnerActivityId);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DuplicateActivityIsRejectedEvenWhenRepeatedGamesAreAllowed(bool unique)
    {
        Slots.Clear();
        AddSlot(current);
        AddSlot(current);
        Set("_custUniqueGames", unique);
        var handler = ConfigureSave();
        await Save();
        Assert.Null(handler.LastWrite);
        Assert.Null(handler.Saved);
        Assert.Equal(0, handler.Reads);
        Assert.Contains("distinct", (string)Get("_customizeError")!);
        Assert.Equal(true, Get("_showCustomize"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MultiSlotEditReducedToOneReloadsAutomaticWinner(bool hadWinner)
    {
        var handler = ConfigureSave();
        await Save();
        Assert.NotNull(handler.Saved);
        handler.Saved.WinnerActivityId = hadWinner ? other.Id : null;
        typeof(Events).GetMethod("ShowEditEvent", Private)!.Invoke(page, [handler.Saved]);
        Call("RemoveSlot", 1);
        await Save();
        Assert.Null(Get("_customizeError"));
        Assert.Equal(HttpMethod.Put, handler.LastWrite);
        Assert.Equal(current.Id, Assert.Single(handler.Saved.Selections).Activity.Id);
        Assert.Equal(current.Id, handler.Saved.WinnerActivityId);
        var listed = Assert.Single((List<EventEntity>)Get("_events")!);
        typeof(Events).GetMethod("ShowDetails", Private)!.Invoke(page, [listed]);
        Assert.Equal(current.Id, ((EventEntity)Get("_detailEvent")!).WinnerActivityId);
    }

    private EventHttpHandler ConfigureSave()
    {
        Set("_custName", "Test event");
        Set("_showCustomize", true);
        var handler = new EventHttpHandler();
        var service = new MW_GC.EventManager.Web.Services.EventService(new HttpClient(handler) { BaseAddress = new Uri("https://example.test") });
        typeof(Events).GetProperty("EventSvc", Private)!.SetValue(page, service);
        return handler;
    }

    private Task Save() => (Task)typeof(Events).GetMethod("SaveCustomizedEvent", Private)!.Invoke(page, null)!;

    private sealed class EventHttpHandler : HttpMessageHandler
    {
        public bool Fail { get; set; }
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
            return new(System.Net.HttpStatusCode.OK) { Content = System.Net.Http.Json.JsonContent.Create(Saved) };
        }
    }
    private IList Slots => (IList)Get("_custSelections")!;
    private List<ActivityEntity> Activities => (List<ActivityEntity>)Get("_activities")!;
    private List<ActivityEntity> Candidates() => (List<ActivityEntity>)Call("GetRerollCandidates", 0)!;
    private object? Get(string name) => typeof(Events).GetField(name, Private)!.GetValue(page);
    private void Set(string name, object value) => typeof(Events).GetField(name, Private)!.SetValue(page, value);
    private object? Call(string name, int index) => typeof(Events).GetMethod(name, Private)!.Invoke(page, [index]);
    private static Guid Id(object slot, string name) => (Guid)slot.GetType().GetProperty(name)!.GetValue(slot)!;
    private void AddSlot(ActivityEntity activity)
    {
        var type = typeof(Events).GetNestedType("SlotSelection", BindingFlags.NonPublic)!;
        var slot = Activator.CreateInstance(type)!;
        type.GetProperty("GameId")!.SetValue(slot, activity.GameId);
        type.GetProperty("ActivityId")!.SetValue(slot, activity.Id);
        Slots.Add(slot);
    }
}
