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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RandomizeAllFillsSlotsAndRerollsKeepIdsUnique(bool unique)
    {
        Set("_custUniqueGames", unique);
        for (var i = 0; i < 100; i++)
        {
            Call("RandomizeAll");
            Assert.Equal(2, Slots.Count);
            Assert.Equal(2, Slots.Cast<object>().Select(s => Id(s, "ActivityId")).Distinct().Count());
            var otherSlot = Slots[1];
            Call("RerollSlot", 0);
            Assert.Same(otherSlot, Slots[1]);
            Assert.Equal(2, Slots.Cast<object>().Select(s => Id(s, "ActivityId")).Distinct().Count());
            Assert.Null(Get("_generationError"));
        }
    }

    [Fact]
    public void SameGameCanFillAllSlotsWithoutDuplicateIds()
    {
        Set("_custUniqueGames", false);
        Set("_custGameIds", new List<Guid> { gameA });
        Activities.Add(current); // Duplicate source records are not extra capacity.
        for (var i = 0; i < 100; i++)
        {
            Call("RandomizeAll");
            Assert.Equal(2, Slots.Count);
            Assert.All(Slots.Cast<object>(), s => Assert.Equal(gameA, Id(s, "GameId")));
            Assert.Equal(2, Slots.Cast<object>().Select(s => Id(s, "ActivityId")).Distinct().Count());
        }
        Call("AddSlot");
        Assert.Equal(2, Slots.Count);
    }

    [Theory]
    [InlineData("_custGameIds")]
    [InlineData("_custThemeIds")]
    [InlineData("_custHolidayIds")]
    public void ImpossibleRandomizePreservesSlotsAndReportsError(string filter)
    {
        var original = Slots;
        Set(filter, new List<Guid> { Guid.NewGuid() });
        Call("RandomizeAll");
        Assert.Same(original, Slots);
        Assert.Equal(2, Slots.Count);
        Assert.False(string.IsNullOrWhiteSpace((string?)Get("_generationError")));
        Set(filter, new List<Guid>());
        Call("RandomizeAll");
        Assert.Null(Get("_generationError"));
    }

    [Fact]
    public void InitialImpossibleGenerationShowsErrorRatherThanSilentlyBlank()
    {
        Slots.Clear();
        Activities.Clear();
        Activities.Add(Activity(Guid.NewGuid()));
        Call("RandomizeAll");
        Assert.Empty(Slots);
        Assert.False(string.IsNullOrWhiteSpace((string?)Get("_generationError")));
    }

    [Fact]
    public void RandomizeAppliesAllFiltersTogether()
    {
        Set("_custUniqueGames", false);
        current.ThemeIds = [theme];
        current.HolidayIds = [holiday];
        Activities.Add(Activity(gameA));
        Set("_custGameIds", new List<Guid> { gameA });
        Set("_custThemeIds", new List<Guid> { theme });
        Set("_custHolidayIds", new List<Guid> { holiday });
        Set("_custThemedOnly", true);
        Call("RandomizeAll");
        Assert.Equal(2, Slots.Count);
        Assert.All(Slots.Cast<object>(), s => Assert.Contains(Id(s, "ActivityId"), new[] { current.Id, replacement.Id }));
        Assert.Equal(2, Slots.Cast<object>().Select(s => Id(s, "ActivityId")).Distinct().Count());
        Assert.Null(Get("_generationError"));
    }

    [Fact]
    public void AddSlotSkipsExhaustedGames()
    {
        Set("_custUniqueGames", false);
        for (var i = 0; i < 100; i++)
        {
            Call("AddSlot");
            Assert.Equal(3, Slots.Count);
            Assert.Equal(replacement.Id, Id(Slots[2]!, "ActivityId"));
            Slots.RemoveAt(2);
        }
    }

    [Fact]
    public void UniqueRandomizeReassignsEarlierActivityInsteadOfFailing()
    {
        Set("_random", new FirstChoiceRandom());
        Set("_custUniqueGames", true);
        other.Id = current.Id;
        Set("_activities", new List<ActivityEntity> { current, replacement, other });
        var source = Activities.ToArray();
        Set("_generationError", "Previous failure");

        for (var run = 0; run < 2; run++)
        {
            Call("RandomizeAll");
            Assert.Null(Get("_generationError"));
            Assert.Equal(2, Slots.Count);
            var selections = Slots.Cast<object>().ToArray();
            Assert.Equal(replacement.Id, Id(selections.Single(s => Id(s, "GameId") == gameA), "ActivityId"));
            Assert.Equal(current.Id, Id(selections.Single(s => Id(s, "GameId") == gameB), "ActivityId"));
            Assert.Equal(source, Activities.ToArray());
        }
    }

    [Fact]
    public void UniqueRandomizeConsidersGamesBeyondAnUnmatchableInitialSubset()
    {
        var third = new GameEntity { Id = Guid.NewGuid() };
        ((List<GameEntity>)Get("_games")!).Add(third);
        other.Id = current.Id;
        var thirdActivity = Activity(third.Id);
        Set("_activities", new List<ActivityEntity> { current, other, thirdActivity });
        Set("_random", new FirstChoiceRandom());
        Set("_generationError", "Previous failure");

        Call("RandomizeAll");

        Assert.Null(Get("_generationError"));
        Assert.Equal(2, Slots.Count);
        var selections = Slots.Cast<object>().ToArray();
        Assert.Equal(current.Id, Id(selections.Single(s => Id(s, "GameId") == gameA), "ActivityId"));
        Assert.Equal(thirdActivity.Id, Id(selections.Single(s => Id(s, "GameId") == third.Id), "ActivityId"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExhaustedGameIsUnavailableAndChoosingItPreservesSlotsAndError(bool unique)
    {
        Set("_custUniqueGames", unique);
        var exhausted = new GameEntity { Id = Guid.NewGuid() };
        ((List<GameEntity>)Get("_games")!).Add(exhausted);
        Activities.Add(new ActivityEntity { GameId = exhausted.Id, Id = other.Id });
        const string error = "Previous generation failure";
        Set("_generationError", error);
        var original = Slots;
        var originalSlots = Slots.Cast<object>().ToArray();

        var available = (List<GameEntity>)Call("GetAvailableGames", 0)!;
        Assert.DoesNotContain(available, g => g.Id == exhausted.Id);
        Assert.Contains(available, g => g.Id == gameA);
        Assert.DoesNotContain(Candidates(), a => a.GameId == exhausted.Id);

        // Defend against a stale dropdown callback as well as filtering options.
        Call("ChangeSlotGame", 0, exhausted);
        Assert.Same(original, Slots);
        Assert.Same(originalSlots[0], Slots[0]);
        Assert.Same(originalSlots[1], Slots[1]);
        Assert.Equal(error, Get("_generationError"));

        // Once a real assignment is available, change only this slot and clear the error.
        var unused = Activity(exhausted.Id);
        Activities.Add(unused);
        Assert.Contains((List<GameEntity>)Call("GetAvailableGames", 0)!, g => g.Id == exhausted.Id);
        Call("ChangeSlotGame", 0, exhausted);
        Assert.Equal(exhausted.Id, Id(Slots[0]!, "GameId"));
        Assert.Equal(unused.Id, Id(Slots[0]!, "ActivityId"));
        Assert.Same(originalSlots[1], Slots[1]);
        Assert.Equal(2, Slots.Count);
        Assert.Null(Get("_generationError"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RerollWithOnlyExhaustedGamesPreservesSlotsAndError(bool unique)
    {
        Set("_custUniqueGames", unique);
        Activities.Remove(replacement);
        var exhausted = new GameEntity { Id = Guid.NewGuid() };
        ((List<GameEntity>)Get("_games")!).Add(exhausted);
        Activities.Add(new ActivityEntity { GameId = exhausted.Id, Id = other.Id });
        const string error = "Previous generation failure";
        Set("_generationError", error);
        var original = Slots;
        var originalSlots = Slots.Cast<object>().ToArray();

        Assert.Empty(Candidates());
        Call("RerollSlot", 0);

        Assert.Same(original, Slots);
        Assert.Same(originalSlots[0], Slots[0]);
        Assert.Same(originalSlots[1], Slots[1]);
        Assert.Equal(2, Slots.Count);
        Assert.Equal(error, Get("_generationError"));
    }

    [Fact]
    public void UniqueRandomizeMatchesSmallGraphFeasibility()
    {
        var games = Enumerable.Range(1, 3).Select(_ => new GameEntity { Id = Guid.NewGuid() }).ToList();
        var ids = Enumerable.Range(1, 3).Select(_ => Guid.NewGuid()).ToArray();
        Set("_games", games);
        Set("_custUniqueGames", true);
        for (var mask = 0; mask < 512; mask++)
        {
            var activities = new List<ActivityEntity>();
            for (var g = 0; g < 3; g++)
                for (var a = 0; a < 3; a++)
                    if ((mask & (1 << (g * 3 + a))) != 0)
                        activities.Add(new ActivityEntity { GameId = games[g].Id, Id = ids[a] });
            Set("_activities", activities);
            for (var count = 1; count <= 3; count++)
            {
                Slots.Clear();
                for (var i = 0; i < count; i++) AddSlot(current);
                var original = Slots;
                Set("_random", new Random(mask));
                Call("RandomizeAll");
                var feasible = count <= Capacity(0, 0);
                Assert.True(feasible == (Get("_generationError") is null), $"Graph {mask}, count {count}");
                if (!feasible)
                {
                    Assert.Same(original, Slots);
                    continue;
                }
                var selections = Slots.Cast<object>().ToArray();
                Assert.Equal(count, selections.Length);
                Assert.Equal(count, selections.Select(s => Id(s, "GameId")).Distinct().Count());
                Assert.Equal(count, selections.Select(s => Id(s, "ActivityId")).Distinct().Count());
                foreach (var slot in selections)
                    Assert.True(activities.Any(a => a.GameId == Id(slot, "GameId") && a.Id == Id(slot, "ActivityId")));
            }

            // Independent exhaustive oracle: skip a game or use an unclaimed ID.
            int Capacity(int game, int used)
            {
                if (game == 3) return 0;
                var best = Capacity(game + 1, used);
                for (var activity = 0; activity < 3; activity++)
                    if ((used & (1 << activity)) == 0 && (mask & (1 << (game * 3 + activity))) != 0)
                        best = Math.Max(best, 1 + Capacity(game + 1, used | (1 << activity)));
                return best;
            }
        }
    }

    [Fact]
    public void UniqueRandomizeRepairsMultiHopChain()
    {
        var games = Enumerable.Range(1, 4).Select(_ => new GameEntity { Id = Guid.NewGuid() }).ToList();
        var ids = Enumerable.Range(1, 4).Select(_ => Guid.NewGuid()).ToArray();
        var activities = new List<ActivityEntity>();
        for (var i = 0; i < 3; i++)
        {
            activities.Add(new ActivityEntity { GameId = games[i].Id, Id = ids[i] });
            activities.Add(new ActivityEntity { GameId = games[i].Id, Id = ids[i + 1] });
        }
        activities.Add(new ActivityEntity { GameId = games[3].Id, Id = ids[0] });
        Set("_games", games);
        Set("_activities", activities);
        Set("_random", new FirstChoiceRandom());
        Slots.Clear();
        foreach (var game in games) AddSlot(Activity(game.Id));
        Call("RandomizeAll");
        Assert.Null(Get("_generationError"));
        Assert.Equal(4, Slots.Count);
        for (var i = 0; i < 4; i++)
            Assert.Equal(ids[(i + 1) % 4], Id(Slots.Cast<object>().Single(s => Id(s, "GameId") == games[i].Id), "ActivityId"));
    }

    [Theory]
    [InlineData("RandomizeAll", true)]
    [InlineData("RandomizeAll", false)]
    [InlineData("RerollSlot", true)]
    [InlineData("ChangeSlotGame", true)]
    [InlineData("ChangeSlotActivity", true)]
    [InlineData("AddSlot", false)]
    [InlineData("RemoveSlot", true)]
    [InlineData("ShowEditEvent", true)]
    [InlineData("ShowCustomize", true)]
    public void SuccessfulSelectionMutationClearsStaleGenerationError(string handler, bool unique)
    {
        Set("_custUniqueGames", unique);
        Set("_random", new FirstChoiceRandom());
        Set("_generationError", "Previous generation failure");
        if (handler == "RemoveSlot") AddSlot(replacement);
        if (handler == "ShowCustomize")
        {
            var game = new GameEntity { Id = Guid.NewGuid() };
            ((List<GameEntity>)Get("_games")!).Add(game);
            Activities.Add(Activity(game.Id));
        }

        switch (handler)
        {
            case "RerollSlot":
                Call(handler, 0);
                Assert.Equal(replacement.Id, Id(Slots[0]!, "ActivityId"));
                break;
            case "ChangeSlotGame":
                var newGame = new GameEntity { Id = Guid.NewGuid() };
                var newActivity = Activity(newGame.Id);
                ((List<GameEntity>)Get("_games")!).Add(newGame);
                Activities.Add(newActivity);
                Call(handler, 0, newGame);
                Assert.Equal(newGame.Id, Id(Slots[0]!, "GameId"));
                Assert.Equal(newActivity.Id, Id(Slots[0]!, "ActivityId"));
                break;
            case "ChangeSlotActivity":
                Call(handler, 0, replacement);
                Assert.Equal(replacement.Id, Id(Slots[0]!, "ActivityId"));
                break;
            case "ShowEditEvent":
                Call(handler, new EventEntity { Name = "Existing event", Selections = [] });
                Assert.Empty(Slots);
                break;
            default:
                if (handler == "RemoveSlot") Call(handler, 2);
                else Call(handler);
                Assert.Equal(handler is "AddSlot" or "ShowCustomize" ? 3 : 2, Slots.Count);
                break;
        }
        Assert.Null(Get("_generationError"));
    }

    [Theory]
    [InlineData("RerollSlot")]
    [InlineData("InvalidReroll")]
    [InlineData("ChangeSlotGame")]
    [InlineData("ChangeSlotActivity")]
    [InlineData("AddSlot")]
    [InlineData("FullAddSlot")]
    [InlineData("RemoveSlot")]
    public void UnchangedSelectionsKeepGenerationError(string handler)
    {
        const string error = "Previous generation failure";
        Set("_generationError", error);
        if (handler == "RerollSlot") Activities.Remove(replacement);
        if (handler == "FullAddSlot")
            while (Slots.Count < 5) AddSlot(replacement);
        // RemoveSlot keeps a floor of one slot, so the no-op case needs exactly one slot.
        if (handler == "RemoveSlot") Slots.RemoveAt(1);
        var original = Slots;
        var originalSlots = Slots.Cast<object>().ToArray();

        switch (handler)
        {
            case "RerollSlot":
            case "RemoveSlot": Call(handler, 0); break;
            case "InvalidReroll": Call("RerollSlot", -1); break;
            case "ChangeSlotGame":
            case "ChangeSlotActivity": Call(handler, 0, null!); break;
            default: Call("AddSlot"); break;
        }

        Assert.Equal(error, Get("_generationError"));
        Assert.Same(original, Slots);
        Assert.Equal(originalSlots, Slots.Cast<object>().ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedRandomizeKeepsSelectionsAndErrorUntilSuccessfulRetry(bool unique)
    {
        Set("_custUniqueGames", unique);
        Set("_random", new FirstChoiceRandom());
        Set("_custGameIds", new List<Guid> { gameB });
        var original = Slots;
        var originalSlots = Slots.Cast<object>().ToArray();
        Call("RandomizeAll");
        var error = Get("_generationError");
        Assert.NotNull(error);
        Call("RandomizeAll");
        Assert.Equal(error, Get("_generationError"));
        Assert.Same(original, Slots);
        Assert.Equal(originalSlots, Slots.Cast<object>().ToArray());

        Set("_custGameIds", new List<Guid>());
        Call("RandomizeAll");
        Assert.Null(Get("_generationError"));
        Assert.Equal(2, Slots.Count);
    }

    [Fact]
    public void FailedCustomizeGenerationRetainsError()
    {
        Activities.Clear();
        Set("_generationError", "Previous failure");
        Call("ShowCustomize");
        Assert.NotNull(Get("_generationError"));
        Assert.Empty(Slots);
        Assert.Equal(true, Get("_showCustomize"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSaveKeepsGenerationErrorAndSelections(bool editing)
    {
        const string error = "Previous generation failure";
        Set("_generationError", error);
        Set("_custName", "Keep this name");
        Set("_showCustomize", true);
        if (editing) Set("_editingEvent", new EventEntity { Id = Guid.NewGuid() });
        using var http = new HttpClient(new FailedSaveHandler()) { BaseAddress = new Uri("https://example.invalid/") };
        typeof(Events).GetProperty("EventSvc", Private)!.SetValue(page,
            new MW_GC.EventManager.Web.Services.EventService(http));
        var original = Slots;
        var originalSlots = Slots.Cast<object>().ToArray();

        await (Task)Call("SaveCustomizedEvent")!;

        Assert.NotNull(Get("_customizeError"));
        Assert.Equal(error, Get("_generationError"));
        Assert.Same(original, Slots);
        Assert.Equal(originalSlots, Slots.Cast<object>().ToArray());
        Assert.Equal("Keep this name", Get("_custName"));
        Assert.Equal(true, Get("_showCustomize"));
        Assert.Equal(false, Get("_generating"));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmbiguousCreateRetryReusesKeyAndNewDialogGetsNewKey(bool timeout)
    {
        Call("RemoveSlot", 1);
        var handler = ConfigureSave();
        handler.LoseWriteResponse = true;
        handler.Timeout = timeout;
        await Save();
        Assert.Equal(true, Get("_showCustomize"));
        Assert.NotNull(Get("_customizeError"));
        var key = Assert.Single(handler.CreateKeys);
        Assert.True(Guid.TryParse(key, out var id));
        Assert.NotEqual(Guid.Empty, id);
        var committedId = handler.Saved!.Id;

        handler.LoseWriteResponse = false;
        await Save();
        Assert.Equal(new[] { key, key }, handler.CreateKeys);
        Assert.Equal(committedId, handler.Saved.Id);
        Assert.Single(handler.Created);
        Assert.Equal(false, Get("_showCustomize"));
        Assert.Null(Get("_customizeError"));
        Assert.Equal(committedId, Assert.Single((List<EventEntity>)Get("_events")!).Id);

        typeof(Events).GetMethod("ShowCustomize", Private)!.Invoke(page, null);
        // ShowCustomize randomizes three slots and keeps none when the pool is smaller;
        // pin the single-slot state this test saves instead of relying on that draw.
        SetSlots(current);
        Set("_custName", "Another event");
        await Save();
        Assert.NotEqual(key, handler.CreateKeys[2]);
        Assert.Equal(2, handler.Created.Count);
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
                Assert.False(request.Headers.Contains("Idempotency-Key"));
            if (LoseWriteResponse)
            {
                if (Timeout) throw new TaskCanceledException("Response timed out after commit");
                throw new HttpRequestException("Response lost after commit");
            }
            return new(System.Net.HttpStatusCode.OK) { Content = System.Net.Http.Json.JsonContent.Create(Saved) };
        }
    }
    private IList Slots => (IList)Get("_custSelections")!;
    private List<ActivityEntity> Activities => (List<ActivityEntity>)Get("_activities")!;
    private List<ActivityEntity> Candidates() => (List<ActivityEntity>)Call("GetRerollCandidates", 0)!;
    private object? Get(string name) => typeof(Events).GetField(name, Private)!.GetValue(page);
    private void Set(string name, object value) => typeof(Events).GetField(name, Private)!.SetValue(page, value);
    private object? Call(string name, params object[] args) => typeof(Events).GetMethod(name, Private)!.Invoke(page, args);
    private static Guid Id(object slot, string name) => (Guid)slot.GetType().GetProperty(name)!.GetValue(slot)!;
    private void AddSlot(ActivityEntity activity)
    {
        var type = typeof(Events).GetNestedType("SlotSelection", BindingFlags.NonPublic)!;
        var slot = Activator.CreateInstance(type)!;
        type.GetProperty("GameId")!.SetValue(slot, activity.GameId);
        type.GetProperty("ActivityId")!.SetValue(slot, activity.Id);
        Slots.Add(slot);
    }

    private void SetSlots(params ActivityEntity[] activities)
    {
        var type = typeof(Events).GetNestedType("SlotSelection", BindingFlags.NonPublic)!;
        var slots = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type))!;
        foreach (var activity in activities)
        {
            var slot = Activator.CreateInstance(type)!;
            type.GetProperty("GameId")!.SetValue(slot, activity.GameId);
            type.GetProperty("ActivityId")!.SetValue(slot, activity.Id);
            slots.Add(slot);
        }
        Set("_custSelections", slots);
    }
}
