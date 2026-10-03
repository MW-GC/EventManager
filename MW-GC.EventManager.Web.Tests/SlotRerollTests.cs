using System.Collections;
using System.Reflection;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;

namespace MW_GC.EventManager.Web.Tests;

// Exercise the actual page handlers without a renderer or network services.
[TestClass]
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

    [TestMethod]
    public void RerollChangesOnlyTargetAndPreservesMetadata()
    {
        Set("_custName", "Keep this name");
        var date = new DateTime(2026, 9, 21);
        Set("_custDate", date);
        var originalOther = Slots[1];
        Call("RerollSlot", 0);
        Assert.AreEqual(replacement.Id, Id(Slots[0]!, "ActivityId"));
        Assert.AreEqual(gameA, Id(Slots[0]!, "GameId"));
        Assert.AreSame(originalOther, Slots[1]);
        Assert.AreEqual(2, Slots.Count);
        Assert.AreEqual("Keep this name", Get("_custName"));
        Assert.AreEqual(date, Get("_custDate"));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void NeverDuplicatesActivitiesOrReturnsCurrent(bool unique)
    {
        Set("_custUniqueGames", unique);
        Assert.AreSequenceEqual(new[] { replacement.Id }, Candidates().Select(a => a.Id));
    }

    [TestMethod]
    public void UniqueGamesExcludesGamesUsedByOtherSlots()
    {
        var alternative = Activity(gameB);
        Activities.Add(alternative);
        Assert.DoesNotContain(alternative, Candidates());
        Set("_custUniqueGames", false);
        Assert.Contains(alternative, Candidates());
    }

    [TestMethod]
    [DataRow("_custGameIds")]
    [DataRow("_custThemeIds")]
    [DataRow("_custHolidayIds")]
    public void SelectedFiltersAreApplied(string field)
    {
        Set(field, new List<Guid> { field == "_custGameIds" ? gameA : field == "_custThemeIds" ? theme : holiday });
        Assert.ContainsSingle(Candidates());
        Set(field, new List<Guid> { Guid.NewGuid() });
        Assert.IsEmpty(Candidates());
        var original = Slots[0];
        Call("RerollSlot", 0);
        Assert.AreSame(original, Slots[0]);
    }

    [TestMethod]
    public void ThemedOnlyAcceptsThemeOrHolidayAndRejectsUntagged()
    {
        var untagged = Activity(gameA);
        Activities.Add(untagged);
        Set("_custThemedOnly", true);
        Assert.DoesNotContain(untagged, Candidates());
        replacement.ThemeIds.Clear();
        Assert.Contains(replacement, Candidates());
        replacement.HolidayIds.Clear();
        Assert.IsEmpty(Candidates());
    }

    [TestMethod]
    public void FiltersCombineRatherThanOverrideEachOther()
    {
        Set("_custGameIds", new List<Guid> { gameA });
        Set("_custThemeIds", new List<Guid> { theme });
        Set("_custHolidayIds", new List<Guid> { holiday });
        Assert.ContainsSingle(Candidates());
        replacement.HolidayIds.Clear();
        Assert.IsEmpty(Candidates());
    }

    [TestMethod]
    public void ExhaustedPoolIsNoOpAndIgnoresOrphanedActivities()
    {
        Activities.Remove(replacement);
        Activities.Add(Activity(Guid.NewGuid()));
        var original = Slots[0];
        Assert.IsEmpty(Candidates());
        Call("RerollSlot", 0);
        Assert.AreSame(original, Slots[0]);
        Assert.AreEqual(2, Slots.Count);
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(2)]
    public void InvalidIndexIsNoOp(int index)
    {
        Assert.IsEmpty((List<ActivityEntity>)Call("GetRerollCandidates", index)!);
        Call("RerollSlot", index);
        Assert.AreEqual(current.Id, Id(Slots[0]!, "ActivityId"));
    }

    [TestMethod]
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
            Assert.AreNotEqual(before, after);
            Assert.Contains(after, eligible);
            Assert.AreSame(originalOther, Slots[1]);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RandomizeAllFillsSlotsAndRerollsKeepIdsUnique(bool unique)
    {
        Set("_custUniqueGames", unique);
        for (var i = 0; i < 100; i++)
        {
            Call("RandomizeAll");
            Assert.AreEqual(2, Slots.Count);
            Assert.AreEqual(2, Slots.Cast<object>().Select(s => Id(s, "ActivityId")).Distinct().Count());
            var otherSlot = Slots[1];
            Call("RerollSlot", 0);
            Assert.AreSame(otherSlot, Slots[1]);
            Assert.AreEqual(2, Slots.Cast<object>().Select(s => Id(s, "ActivityId")).Distinct().Count());
            Assert.IsNull(Get("_generationError"));
        }
    }

    [TestMethod]
    public void SameGameCanFillAllSlotsWithoutDuplicateIds()
    {
        Set("_custUniqueGames", false);
        Set("_custGameIds", new List<Guid> { gameA });
        Activities.Add(current); // Duplicate source records are not extra capacity.
        for (var i = 0; i < 100; i++)
        {
            Call("RandomizeAll");
            Assert.AreEqual(2, Slots.Count);
            foreach (var s in Slots.Cast<object>()) Assert.AreEqual(gameA, Id(s, "GameId"));
            Assert.AreEqual(2, Slots.Cast<object>().Select(s => Id(s, "ActivityId")).Distinct().Count());
        }
        Call("AddSlot");
        Assert.AreEqual(2, Slots.Count);
    }

    [TestMethod]
    [DataRow("_custGameIds")]
    [DataRow("_custThemeIds")]
    [DataRow("_custHolidayIds")]
    public void ImpossibleRandomizePreservesSlotsAndReportsError(string filter)
    {
        var original = Slots;
        Set(filter, new List<Guid> { Guid.NewGuid() });
        Call("RandomizeAll");
        Assert.AreSame(original, Slots);
        Assert.AreEqual(2, Slots.Count);
        Assert.IsFalse(string.IsNullOrWhiteSpace((string?)Get("_generationError")));
        Set(filter, new List<Guid>());
        Call("RandomizeAll");
        Assert.IsNull(Get("_generationError"));
    }

    [TestMethod]
    public void InitialImpossibleGenerationShowsErrorRatherThanSilentlyBlank()
    {
        Slots.Clear();
        Activities.Clear();
        Activities.Add(Activity(Guid.NewGuid()));
        Call("RandomizeAll");
        Assert.IsEmpty(Slots);
        Assert.IsFalse(string.IsNullOrWhiteSpace((string?)Get("_generationError")));
    }

    [TestMethod]
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
        Assert.AreEqual(2, Slots.Count);
        foreach (var s in Slots.Cast<object>()) Assert.Contains(Id(s, "ActivityId"), new[] { current.Id, replacement.Id });
        Assert.AreEqual(2, Slots.Cast<object>().Select(s => Id(s, "ActivityId")).Distinct().Count());
        Assert.IsNull(Get("_generationError"));
    }

    [TestMethod]
    public void AddSlotSkipsExhaustedGames()
    {
        Set("_custUniqueGames", false);
        for (var i = 0; i < 100; i++)
        {
            Call("AddSlot");
            Assert.AreEqual(3, Slots.Count);
            Assert.AreEqual(replacement.Id, Id(Slots[2]!, "ActivityId"));
            Slots.RemoveAt(2);
        }
    }

    [TestMethod]
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
            Assert.IsNull(Get("_generationError"));
            Assert.AreEqual(2, Slots.Count);
            var selections = Slots.Cast<object>().ToArray();
            Assert.AreEqual(replacement.Id, Id(selections.Single(s => Id(s, "GameId") == gameA), "ActivityId"));
            Assert.AreEqual(current.Id, Id(selections.Single(s => Id(s, "GameId") == gameB), "ActivityId"));
            Assert.AreSequenceEqual(source, Activities.ToArray());
        }
    }

    [TestMethod]
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

        Assert.IsNull(Get("_generationError"));
        Assert.AreEqual(2, Slots.Count);
        var selections = Slots.Cast<object>().ToArray();
        Assert.AreEqual(current.Id, Id(selections.Single(s => Id(s, "GameId") == gameA), "ActivityId"));
        Assert.AreEqual(thirdActivity.Id, Id(selections.Single(s => Id(s, "GameId") == third.Id), "ActivityId"));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
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
        Assert.DoesNotContain(g => g.Id == exhausted.Id, available);
        Assert.Contains(g => g.Id == gameA, available);
        Assert.DoesNotContain(a => a.GameId == exhausted.Id, Candidates());

        // Defend against a stale dropdown callback as well as filtering options.
        Call("ChangeSlotGame", 0, exhausted);
        Assert.AreSame(original, Slots);
        Assert.AreSame(originalSlots[0], Slots[0]);
        Assert.AreSame(originalSlots[1], Slots[1]);
        Assert.AreEqual(error, Get("_generationError"));

        // Once a real assignment is available, change only this slot and clear the error.
        var unused = Activity(exhausted.Id);
        Activities.Add(unused);
        Assert.Contains(g => g.Id == exhausted.Id, (List<GameEntity>)Call("GetAvailableGames", 0)!);
        Call("ChangeSlotGame", 0, exhausted);
        Assert.AreEqual(exhausted.Id, Id(Slots[0]!, "GameId"));
        Assert.AreEqual(unused.Id, Id(Slots[0]!, "ActivityId"));
        Assert.AreSame(originalSlots[1], Slots[1]);
        Assert.AreEqual(2, Slots.Count);
        Assert.IsNull(Get("_generationError"));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
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

        Assert.IsEmpty(Candidates());
        Call("RerollSlot", 0);

        Assert.AreSame(original, Slots);
        Assert.AreSame(originalSlots[0], Slots[0]);
        Assert.AreSame(originalSlots[1], Slots[1]);
        Assert.AreEqual(2, Slots.Count);
        Assert.AreEqual(error, Get("_generationError"));
    }

    [TestMethod]
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
                Assert.IsTrue(feasible == (Get("_generationError") is null), $"Graph {mask}, count {count}");
                if (!feasible)
                {
                    Assert.AreSame(original, Slots);
                    continue;
                }
                var selections = Slots.Cast<object>().ToArray();
                Assert.AreEqual(count, selections.Length);
                Assert.AreEqual(count, selections.Select(s => Id(s, "GameId")).Distinct().Count());
                Assert.AreEqual(count, selections.Select(s => Id(s, "ActivityId")).Distinct().Count());
                foreach (var slot in selections)
                    Assert.Contains(a => a.GameId == Id(slot, "GameId") && a.Id == Id(slot, "ActivityId"), activities);
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

    [TestMethod]
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
        Assert.IsNull(Get("_generationError"));
        Assert.AreEqual(4, Slots.Count);
        for (var i = 0; i < 4; i++)
            Assert.AreEqual(ids[(i + 1) % 4], Id(Slots.Cast<object>().Single(s => Id(s, "GameId") == games[i].Id), "ActivityId"));
    }

    [TestMethod]
    [DataRow("RandomizeAll", true)]
    [DataRow("RandomizeAll", false)]
    [DataRow("RerollSlot", true)]
    [DataRow("ChangeSlotGame", true)]
    [DataRow("ChangeSlotActivity", true)]
    [DataRow("AddSlot", false)]
    [DataRow("RemoveSlot", true)]
    [DataRow("ShowEditEvent", true)]
    [DataRow("ShowCustomize", true)]
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
                Assert.AreEqual(replacement.Id, Id(Slots[0]!, "ActivityId"));
                break;
            case "ChangeSlotGame":
                var newGame = new GameEntity { Id = Guid.NewGuid() };
                var newActivity = Activity(newGame.Id);
                ((List<GameEntity>)Get("_games")!).Add(newGame);
                Activities.Add(newActivity);
                Call(handler, 0, newGame);
                Assert.AreEqual(newGame.Id, Id(Slots[0]!, "GameId"));
                Assert.AreEqual(newActivity.Id, Id(Slots[0]!, "ActivityId"));
                break;
            case "ChangeSlotActivity":
                Call(handler, 0, replacement);
                Assert.AreEqual(replacement.Id, Id(Slots[0]!, "ActivityId"));
                break;
            case "ShowEditEvent":
                Call(handler, new EventEntity { Name = "Existing event", Selections = [] });
                Assert.IsEmpty(Slots);
                break;
            default:
                if (handler == "RemoveSlot") Call(handler, 2);
                else Call(handler);
                Assert.AreEqual(handler is "AddSlot" or "ShowCustomize" ? 3 : 2, Slots.Count);
                break;
        }
        Assert.IsNull(Get("_generationError"));
    }

    [TestMethod]
    [DataRow("RerollSlot")]
    [DataRow("InvalidReroll")]
    [DataRow("ChangeSlotGame")]
    [DataRow("ChangeSlotActivity")]
    [DataRow("AddSlot")]
    [DataRow("FullAddSlot")]
    [DataRow("RemoveSlot")]
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

        Assert.AreEqual(error, Get("_generationError"));
        Assert.AreSame(original, Slots);
        Assert.AreSequenceEqual(originalSlots, Slots.Cast<object>().ToArray());
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void FailedRandomizeKeepsSelectionsAndErrorUntilSuccessfulRetry(bool unique)
    {
        Set("_custUniqueGames", unique);
        Set("_random", new FirstChoiceRandom());
        Set("_custGameIds", new List<Guid> { gameB });
        var original = Slots;
        var originalSlots = Slots.Cast<object>().ToArray();
        Call("RandomizeAll");
        var error = Get("_generationError");
        Assert.IsNotNull(error);
        Call("RandomizeAll");
        Assert.AreEqual(error, Get("_generationError"));
        Assert.AreSame(original, Slots);
        Assert.AreSequenceEqual(originalSlots, Slots.Cast<object>().ToArray());

        Set("_custGameIds", new List<Guid>());
        Call("RandomizeAll");
        Assert.IsNull(Get("_generationError"));
        Assert.AreEqual(2, Slots.Count);
    }

    [TestMethod]
    public void FailedCustomizeGenerationRetainsError()
    {
        Activities.Clear();
        Set("_generationError", "Previous failure");
        Call("ShowCustomize");
        Assert.IsNotNull(Get("_generationError"));
        Assert.IsEmpty(Slots);
        Assert.AreEqual(true, Get("_showCustomize"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
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

        Assert.IsNotNull(Get("_customizeError"));
        Assert.AreEqual(error, Get("_generationError"));
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
    public void RemoveAllowsOneSlotButNeverRemovesLastSlot()
    {
        Call("RemoveSlot", 1);
        Assert.ContainsSingle(Slots.Cast<object>());
        Call("RemoveSlot", 0);
        Assert.ContainsSingle(Slots.Cast<object>());
        Assert.AreEqual(current.Id, Id(Slots[0]!, "ActivityId"));
    }

    [TestMethod]
    public async Task OneSlotCreateAndEditSendAutomaticWinnerAndReload()
    {
        Call("RemoveSlot", 1);
        Set("_custName", "One activity");
        var handler = new EventHttpHandler();
        var service = new MW_GC.EventManager.Web.Services.EventService(new HttpClient(handler) { BaseAddress = new Uri("https://example.test") });
        typeof(Events).GetProperty("EventSvc", Private)!.SetValue(page, service);
        await (Task)typeof(Events).GetMethod("SaveCustomizedEvent", Private)!.Invoke(page, null)!;
        Assert.AreEqual(HttpMethod.Post, handler.LastWrite);
        Assert.AreEqual(current.Id, handler.Saved!.WinnerActivityId);
        Assert.AreEqual(current.Id, Assert.ContainsSingle((List<EventEntity>)Get("_events")!).WinnerActivityId);

        typeof(Events).GetMethod("ShowEditEvent", Private)!.Invoke(page, [handler.Saved]);
        Assert.ContainsSingle(Slots.Cast<object>());
        Set("_custGameIds", new List<Guid> { gameA });
        Assert.AreEqual(replacement.Id, Assert.ContainsSingle(Candidates()).Id);
        Call("RerollSlot", 0);
        var replacementId = Id(Slots[0]!, "ActivityId");
        await (Task)typeof(Events).GetMethod("SaveCustomizedEvent", Private)!.Invoke(page, null)!;
        Assert.AreEqual(HttpMethod.Put, handler.LastWrite);
        Assert.AreEqual(replacementId, handler.Saved!.WinnerActivityId);
        Assert.AreEqual("One activity", handler.Saved.Name);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void OneSlotRandomizationAndRerollRespectFilters(bool unique)
    {
        Call("RemoveSlot", 1);
        Set("_custUniqueGames", unique);
        Set("_custGameIds", new List<Guid> { gameA });
        Set("_custThemeIds", new List<Guid> { theme });
        Set("_custHolidayIds", new List<Guid> { holiday });
        Set("_custThemedOnly", true);
        typeof(Events).GetMethod("RandomizeAll", Private)!.Invoke(page, null);
        Assert.ContainsSingle(Slots.Cast<object>());
        Assert.AreEqual(replacement.Id, Id(Slots[0]!, "ActivityId"));
        Assert.IsEmpty(Candidates());
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(6)]
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
        Assert.IsNull(handler.LastWrite);
        Assert.Contains("Select 1–5", (string)Get("_customizeError")!);
        Assert.AreEqual(true, Get("_showCustomize"));
        Assert.AreEqual(false, Get("_generating"));
    }

    [TestMethod]
    public async Task IncompleteSlotIsNotSilentlyDroppedIntoAnAutomaticWinner()
    {
        Slots[1]!.GetType().GetProperty("ActivityId")!.SetValue(Slots[1], Guid.Empty);
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
        Call("RemoveSlot", 1);
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
        typeof(Events).GetMethod("ShowEditEvent", Private)!.Invoke(page, [handler.Saved]);
        if (replaceWinner) Call("RerollSlot", 0);
        await Save();
        Assert.AreEqual(HttpMethod.Put, handler.LastWrite);
        Assert.AreEqual(2, handler.Saved!.Selections.Count);
        Assert.AreEqual(replaceWinner ? (Guid?)null : current.Id, handler.Saved.WinnerActivityId);
    }

    [TestMethod]
    public async Task SavedSoleWinnerIsReadBackForListAndDetails()
    {
        Call("RemoveSlot", 1);
        var handler = ConfigureSave();
        await Save();
        var persisted = Assert.ContainsSingle((List<EventEntity>)Get("_events")!);
        Assert.AreNotSame(handler.Saved, persisted);
        typeof(Events).GetMethod("ShowDetails", Private)!.Invoke(page, [persisted]);
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
        Call("RemoveSlot", 1);
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
        Slots.Clear();
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
        Slots.Clear();
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
        typeof(Events).GetMethod("ShowEditEvent", Private)!.Invoke(page, [handler.Saved]);
        Call("RemoveSlot", 1);
        await Save();
        Assert.IsNull(Get("_customizeError"));
        Assert.AreEqual(HttpMethod.Put, handler.LastWrite);
        Assert.AreEqual(current.Id, Assert.ContainsSingle(handler.Saved.Selections).Activity.Id);
        Assert.AreEqual(current.Id, handler.Saved.WinnerActivityId);
        var listed = Assert.ContainsSingle((List<EventEntity>)Get("_events")!);
        typeof(Events).GetMethod("ShowDetails", Private)!.Invoke(page, [listed]);
        Assert.AreEqual(current.Id, ((EventEntity)Get("_detailEvent")!).WinnerActivityId);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AmbiguousCreateRetryReusesKeyAndNewDialogGetsNewKey(bool timeout)
    {
        Call("RemoveSlot", 1);
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

        typeof(Events).GetMethod("ShowCustomize", Private)!.Invoke(page, null);
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
                Assert.IsFalse(request.Headers.Contains("Idempotency-Key"));
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
        Assert.IsNull(Get("_generationError"));
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
        Set("_generationError", "Previous failure");
        Call("ShowCustomize");
        AssertFilledSlots(expectedSlots, distinctGames: true);
        Assert.AreEqual(true, Get("_showCustomize"));
    }

    [TestMethod]
    [DataRow(1, 1)]
    [DataRow(2, 2)]
    [DataRow(5, 3)]
    public void FreshRandomizeWithRepeatedGamesAllowedSizesSlotsToTheActivities(int activities, int expectedSlots)
    {
        UseLibrary(activities);
        Slots.Clear();
        Set("_custUniqueGames", false);
        Call("RandomizeAll");
        AssertFilledSlots(expectedSlots, distinctGames: false);
    }

    [TestMethod]
    public void FreshCustomizeWithNoActivitiesReportsNoMatchRatherThanKeptSelections()
    {
        UseLibrary(0, 0);
        Set("_generationError", "Previous failure");
        Call("ShowCustomize");
        Assert.IsEmpty(Slots);
        Assert.AreEqual(NoMatchMessage, Get("_generationError"));
        Assert.AreEqual(true, Get("_showCustomize"));
    }

    [TestMethod]
    [DataRow("_custGameIds", true)]
    [DataRow("_custGameIds", false)]
    [DataRow("_custThemeIds", true)]
    [DataRow("_custThemeIds", false)]
    [DataRow("_custHolidayIds", true)]
    [DataRow("_custHolidayIds", false)]
    public void FreshRandomizeWithFiltersExcludingEverythingReportsNoMatch(string filter, bool unique)
    {
        UseLibrary(1, 1, 1);
        Slots.Clear();
        Set("_custUniqueGames", unique);
        Set(filter, new List<Guid> { Guid.NewGuid() });
        Call("RandomizeAll");
        Assert.IsEmpty(Slots);
        Assert.AreEqual(NoMatchMessage, Get("_generationError"));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RandomizeAllWithExistingSelectionsKeepsThemAndSaysSo(bool unique)
    {
        var games = UseLibrary(1, 1, 1);
        SetSlots(Activities.ToArray());
        Set("_custUniqueGames", unique);
        Set("_custGameIds", new List<Guid> { games[0].Id, games[1].Id });
        var original = Slots;
        var originalSlots = Slots.Cast<object>().ToArray();
        Call("RandomizeAll");
        Assert.AreSame(original, Slots);
        Assert.AreSequenceEqual(originalSlots, Slots.Cast<object>().ToArray());
        Assert.Contains("Existing selections were kept", (string)Get("_generationError")!);
    }

    private const string AddSlotNoMatchReason = "No unused activity matches the current filters. Adjust the filters or add activities to the library.";

    private string? AddSlotBlockedReason() => (string?)Call("GetAddSlotBlockedReason");
    private List<ActivityEntity> AddSlotCandidates() => (List<ActivityEntity>)Call("GetAddSlotCandidates")!;

    // A blocked Add Activity Slot keeps every slot, keeps the generation error and says why.
    private void AssertAddSlotRefused(string expectedReason)
    {
        const string error = "Previous generation failure";
        Set("_generationError", error);
        var original = Slots;
        var originalSlots = Slots.Cast<object>().ToArray();
        Assert.AreEqual(expectedReason, AddSlotBlockedReason());

        Call("AddSlot");

        Assert.AreSame(original, Slots);
        Assert.AreSequenceEqual(originalSlots, Slots.Cast<object>().ToArray());
        Assert.AreEqual(expectedReason, Get("_addSlotError"));
        Assert.AreEqual(error, Get("_generationError"));
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

    [TestMethod]
    public void UniqueGamesBlocksAddWhenOnlyUsedGamesHaveFreeActivities()
    {
        var games = UseLibrary(2, 1, 1);
        var free = Activities[1];
        SetSlots(Activities[0], Activities[2], Activities[3]);
        Set("_custUniqueGames", true);
        Assert.IsEmpty(AddSlotCandidates());
        AssertAddSlotRefused("Every Game with an unused activity is already in a slot. Turn off Unique games only to repeat a Game.");

        Set("_custUniqueGames", false);
        Assert.IsNull(AddSlotBlockedReason());
        Assert.AreEqual(free.Id, Assert.ContainsSingle(AddSlotCandidates()).Id);
        Call("AddSlot");
        Assert.AreEqual(4, Slots.Count);
        Assert.AreEqual(free.Id, Id(Slots[3]!, "ActivityId"));
        Assert.AreEqual(games[0].Id, Id(Slots[3]!, "GameId"));
        Assert.IsNull(Get("_addSlotError"));
        Assert.AreEqual(4, Slots.Cast<object>().Select(s => Id(s, "ActivityId")).Distinct().Count());
    }

    [TestMethod]
    [DataRow("_custGameIds", true)]
    [DataRow("_custGameIds", false)]
    [DataRow("_custThemeIds", true)]
    [DataRow("_custThemeIds", false)]
    [DataRow("_custHolidayIds", true)]
    [DataRow("_custHolidayIds", false)]
    public void FiltersExcludingEveryUnusedActivityDisableAddWithReason(string filter, bool unique)
    {
        var games = UseLibrary(1, 1, 1, 1);
        SetSlots(Activities[0], Activities[1], Activities[2]);
        Set("_custUniqueGames", unique);
        Set(filter, new List<Guid> { Guid.NewGuid() });
        Assert.IsEmpty(AddSlotCandidates());
        AssertAddSlotRefused(AddSlotNoMatchReason);

        // Clearing the filter frees the fourth Game: one click adds exactly that one slot.
        Set(filter, new List<Guid>());
        Assert.IsNull(AddSlotBlockedReason());
        Call("AddSlot");
        Assert.AreEqual(4, Slots.Count);
        Assert.AreEqual(Activities[3].Id, Id(Slots[3]!, "ActivityId"));
        Assert.AreEqual(games[3].Id, Id(Slots[3]!, "GameId"));
        Assert.IsNull(Get("_addSlotError"));
        Assert.IsNull(Get("_generationError"));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void AddSlotAddsExactlyOneUniqueSlotPerClickUntilTheLimit(bool unique)
    {
        UseLibrary(2, 2, 2, 2, 2, 2);
        Set("_custUniqueGames", unique);
        for (var run = 0; run < 50; run++)
        {
            SetSlots(Activities[0]);
            for (var expected = 2; expected <= EventEntity.MaximumSelections; expected++)
            {
                Assert.IsNull(AddSlotBlockedReason());
                Call("AddSlot");
                Assert.AreEqual(expected, Slots.Count);
                var slots = Slots.Cast<object>().ToArray();
                Assert.AreEqual(expected, slots.Select(s => Id(s, "ActivityId")).Distinct().Count());
                if (unique) Assert.AreEqual(expected, slots.Select(s => Id(s, "GameId")).Distinct().Count());
                foreach (var s in slots) Assert.Contains(a => a.Id == Id(s, "ActivityId") && a.GameId == Id(s, "GameId"), Activities);
            }
            AssertAddSlotRefused("An Event holds at most 5 activities.");
        }
    }

    [TestMethod]
    public void RerollAndAddSlotShareOneCandidatePool()
    {
        // Same filters, same used Activities, same Unique games rule: the slot being re-rolled
        // is the only difference, so removing it makes the re-roll pool the add pool.
        Activities.Add(Activity(gameA));
        Activities.Add(Activity(gameB));
        Activities.Add(Activity(Guid.NewGuid()));
        foreach (var unique in new[] { true, false })
        {
            Set("_custUniqueGames", unique);
            var rerollPool = Candidates().Select(a => a.Id).ToHashSet();
            var removed = Slots[0]!;
            Slots.RemoveAt(0);
            var addPool = AddSlotCandidates().Select(a => a.Id).ToHashSet();
            Slots.Insert(0, removed);
            addPool.Remove(current.Id);
            CollectionAssert.AreEquivalent(addPool.ToList(), rerollPool.ToList());
        }
    }

    // Scenarios carried over from the API generator tests that #48 removed: the page is now the
    // only place that draws slots, so it must keep these rules.

    [TestMethod]
    public void RepeatedGamesExhaustPoolWithoutRepeatingIds()
    {
        var games = UseLibrary(5);
        Activities.Add(Activities[0]); // Duplicate records do not add capacity.
        var library = Activities.ToArray();
        Set("_custUniqueGames", false);
        for (var i = 0; i < 100; i++)
        {
            SetSlots(Enumerable.Repeat(Activities[0], 5).ToArray());
            Call("RandomizeAll");
            AssertFilledSlots(5, distinctGames: false);
            foreach (var s in Slots.Cast<object>()) Assert.AreEqual(games[0].Id, Id(s, "GameId"));
        }
        Assert.AreSequenceEqual(library, Activities.ToArray());

        // Four distinct ids plus the duplicate cannot fill five slots.
        Activities.Remove(library[4]);
        var original = Slots;
        var originalSlots = Slots.Cast<object>().ToArray();
        Call("RandomizeAll");
        Assert.AreSame(original, Slots);
        Assert.AreSequenceEqual(originalSlots, Slots.Cast<object>().ToArray());
        Assert.IsNotNull(Get("_generationError"));

        // One Game cannot fill two slots with Unique games only on.
        SetSlots(library[0], library[1]);
        Set("_custUniqueGames", true);
        Set("_generationError", null!);
        original = Slots;
        Call("RandomizeAll");
        Assert.AreSame(original, Slots);
        Assert.IsNotNull(Get("_generationError"));
    }

    [TestMethod]
    public void SkewedPoolAlwaysFillsEverySlot()
    {
        // One Game holds almost every Activity; four Games hold one each.
        UseLibrary(50, 1, 1, 1, 1);
        Set("_custUniqueGames", false);
        for (var i = 0; i < 100; i++)
        {
            SetSlots(Enumerable.Repeat(Activities[0], 5).ToArray());
            Call("RandomizeAll");
            AssertFilledSlots(5, distinctGames: false);
            foreach (var s in Slots.Cast<object>())
                Assert.Contains(a => a.Id == Id(s, "ActivityId") && a.GameId == Id(s, "GameId"), Activities);
        }
    }

    [TestMethod]
    public void SkewedPoolExhaustsEveryDistinctIdWithoutMutatingInputs()
    {
        // Five distinct ids, two of them on one Game, plus duplicate records of that Game's ids.
        var games = UseLibrary(2, 1, 1, 1);
        var expected = Activities.Select(a => a.Id).ToArray();
        Activities.AddRange(Activities.Take(2).ToArray());
        var library = Activities.ToArray();
        var originalGames = games.ToArray();
        Set("_custUniqueGames", false);

        // Reuse the same page and inputs to catch state leaking between draws.
        for (var run = 0; run < 50; run++)
        {
            SetSlots(Enumerable.Repeat(Activities[0], 5).ToArray());
            Call("RandomizeAll");
            AssertFilledSlots(5, distinctGames: false);
            CollectionAssert.AreEquivalent(expected, Slots.Cast<object>().Select(s => Id(s, "ActivityId")).ToArray());
            foreach (var s in Slots.Cast<object>())
                Assert.Contains(a => a.Id == Id(s, "ActivityId") && a.GameId == Id(s, "GameId"), Activities);
            Assert.AreSequenceEqual(library, Activities.ToArray());
            Assert.AreSequenceEqual(originalGames, ((List<GameEntity>)Get("_games")!).ToArray());
        }
    }

    [TestMethod]
    public void DuplicateIdsAcrossPoolsAreRemovedTogetherIncludingExhaustedGames()
    {
        var games = UseLibrary(Enumerable.Repeat(0, 10).ToArray());
        var (one, two, three) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        // Ids one and two appear in every Game (one twice per Game); id three only in the first.
        var activities = games.SelectMany(g => new[]
        {
            new ActivityEntity { Id = one, GameId = g.Id },
            new ActivityEntity { Id = one, GameId = g.Id },
            new ActivityEntity { Id = two, GameId = g.Id },
        }).ToList();
        activities.Add(new ActivityEntity { Id = three, GameId = games[0].Id });
        Set("_activities", activities);
        Set("_custUniqueGames", false);

        for (var run = 0; run < 20; run++)
        {
            SetSlots(activities[0], activities[0], activities[0]);
            Call("RandomizeAll");
            AssertFilledSlots(3, distinctGames: false);
            CollectionAssert.AreEquivalent(new[] { one, two, three }, Slots.Cast<object>().Select(s => Id(s, "ActivityId")).ToArray());
            foreach (var s in Slots.Cast<object>())
                Assert.Contains(a => a.Id == Id(s, "ActivityId") && a.GameId == Id(s, "GameId"), activities);
        }

        // Three distinct ids cannot fill four slots, however many Games repeat them.
        SetSlots(activities[0], activities[0], activities[0], activities[0]);
        var original = Slots;
        Call("RandomizeAll");
        Assert.AreSame(original, Slots);
        Assert.IsNotNull(Get("_generationError"));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void IndexedPoolsRespectCombinedFiltersAndIgnoreOrphanCapacity(bool unique)
    {
        var orphan = Guid.NewGuid(); // Selected in the Game filter, but no such Game exists.
        ActivityEntity Tagged(Guid game, bool withTheme, bool withHoliday)
        {
            var a = Activity(game);
            if (withTheme) a.ThemeIds = [theme];
            if (withHoliday) a.HolidayIds = [holiday];
            return a;
        }
        var eligible = Tagged(gameA, true, true);
        var wrongGame = Tagged(gameB, true, true);
        var orphanActivity = Tagged(orphan, true, true); // Passes every filter but has no known Game.
        var themeOnly = Tagged(gameA, true, false);
        var holidayOnly = Tagged(gameA, false, true);
        var unthemed = Tagged(gameA, false, false);
        Set("_activities", new List<ActivityEntity> { eligible, wrongGame, orphanActivity, themeOnly, holidayOnly, unthemed });
        Set("_custUniqueGames", unique);
        Set("_custThemedOnly", true);
        Set("_custGameIds", new List<Guid> { gameA, orphan });
        Set("_custThemeIds", new List<Guid> { theme });
        Set("_custHolidayIds", new List<Guid> { holiday });

        SetSlots(current);
        Call("RandomizeAll");
        AssertFilledSlots(1, distinctGames: true);
        Assert.AreEqual(eligible.Id, Id(Slots[0]!, "ActivityId"));
        Assert.AreEqual(gameA, Id(Slots[0]!, "GameId"));

        // The orphan's Activity must not add capacity ...
        SetSlots(current, other);
        var original = Slots;
        Call("RandomizeAll");
        Assert.AreSame(original, Slots);
        Assert.IsNotNull(Get("_generationError"));

        // ... and no Games means no capacity at all.
        var library = (List<GameEntity>)Get("_games")!;
        Set("_games", new List<GameEntity>());
        SetSlots(current);
        original = Slots;
        Call("RandomizeAll");
        Assert.AreSame(original, Slots);
        Assert.IsNotNull(Get("_generationError"));
        Set("_games", library);

        // Dropping the Theme filter admits the holiday-only Activity (themed via its Holiday).
        Set("_activities", new List<ActivityEntity> { holidayOnly });
        Set("_custThemeIds", new List<Guid>());
        Call("RandomizeAll");
        AssertFilledSlots(1, distinctGames: true);
        Assert.AreEqual(holidayOnly.Id, Id(Slots[0]!, "ActivityId"));

        // An unthemed Activity stays excluded while Themed activities only is on ...
        Set("_activities", new List<ActivityEntity> { unthemed });
        Set("_custHolidayIds", new List<Guid>());
        original = Slots;
        Call("RandomizeAll");
        Assert.AreSame(original, Slots);
        Assert.IsNotNull(Get("_generationError"));

        // ... and is admitted once every Filter is cleared.
        Set("_custThemedOnly", false);
        Set("_custGameIds", new List<Guid>());
        Call("RandomizeAll");
        AssertFilledSlots(1, distinctGames: true);
        Assert.AreEqual(unthemed.Id, Id(Slots[0]!, "ActivityId"));
    }

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

        Call("RandomizeAll");

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

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RandomizeAllAcceptsUpperBoundaryWithoutRepeatingActivities(bool unique)
    {
        // With repeated Games allowed, use one Game so all five slots must share it.
        UseLibrary(unique ? new[] { 1, 1, 1, 1, 1 } : new[] { 5 });
        Set("_custUniqueGames", unique);
        for (var i = 0; i < 100; i++)
        {
            SetSlots(Enumerable.Repeat(Activities[0], EventEntity.MaximumSelections).ToArray());
            Call("RandomizeAll");
            AssertFilledSlots(EventEntity.MaximumSelections, distinctGames: unique);
        }
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
        typeof(Events).GetProperty("ActivitySvc", Private)!.SetValue(page,
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
