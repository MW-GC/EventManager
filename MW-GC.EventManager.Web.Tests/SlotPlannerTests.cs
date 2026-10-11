using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Tests;

// The customize dialog's slot-planning rules (issue #58), driven through SlotPlanner's public API.
// These tests moved from SlotRerollTests, which reached the same rules through the Events page's
// private members; their names and assertions are unchanged.
[TestClass]
public class SlotPlannerTests
{
    private readonly Guid gameA = Guid.NewGuid();
    private readonly Guid gameB = Guid.NewGuid();
    private readonly Guid theme = Guid.NewGuid();
    private readonly Guid holiday = Guid.NewGuid();
    private readonly ActivityEntity current;
    private readonly ActivityEntity other;
    private readonly ActivityEntity replacement;
    private List<GameEntity> games;
    private List<ActivityEntity> activities;
    private SlotPlanner planner;

    public SlotPlannerTests()
    {
        current = Activity(gameA);
        other = Activity(gameB);
        replacement = Activity(gameA);
        replacement.ThemeIds = [theme];
        replacement.HolidayIds = [holiday];
        games = [new() { Id = gameA }, new() { Id = gameB }];
        activities = [current, other, replacement];
        planner = NewPlanner(Random.Shared);
        AddSlot(current);
        AddSlot(other);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void NeverDuplicatesActivitiesOrReturnsCurrent(bool unique)
    {
        Unique(unique);
        Assert.AreSequenceEqual(new[] { replacement.Id }, Candidates().Select(a => a.Id));
    }

    [TestMethod]
    public void UniqueGamesExcludesGamesUsedByOtherSlots()
    {
        var alternative = Activity(gameB);
        Activities.Add(alternative);
        Assert.DoesNotContain(alternative, Candidates());
        Unique(false);
        Assert.Contains(alternative, Candidates());
    }

    [TestMethod]
    [DataRow("_custGameIds")]
    [DataRow("_custThemeIds")]
    [DataRow("_custHolidayIds")]
    public void SelectedFiltersAreApplied(string field)
    {
        SetIds(field, field == "_custGameIds" ? gameA : field == "_custThemeIds" ? theme : holiday);
        Assert.ContainsSingle(Candidates());
        SetIds(field, Guid.NewGuid());
        Assert.IsEmpty(Candidates());
        var original = Slots[0];
        planner.RerollSlot(0);
        Assert.AreSame(original, Slots[0]);
    }

    [TestMethod]
    public void ThemedOnlyAcceptsThemeOrHolidayAndRejectsUntagged()
    {
        var untagged = Activity(gameA);
        Activities.Add(untagged);
        planner.Filter = planner.Filter with { ThemedOnly = true };
        Assert.DoesNotContain(untagged, Candidates());
        replacement.ThemeIds.Clear();
        Assert.Contains(replacement, Candidates());
        replacement.HolidayIds.Clear();
        Assert.IsEmpty(Candidates());
    }

    [TestMethod]
    public void FiltersCombineRatherThanOverrideEachOther()
    {
        SetIds("_custGameIds", gameA);
        SetIds("_custThemeIds", theme);
        SetIds("_custHolidayIds", holiday);
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
        planner.RerollSlot(0);
        Assert.AreSame(original, Slots[0]);
        Assert.AreEqual(2, Slots.Count);
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(2)]
    public void InvalidIndexIsNoOp(int index)
    {
        Assert.IsEmpty(planner.GetRerollCandidates(index));
        planner.RerollSlot(index);
        Assert.AreEqual(current.Id, Slots[0].ActivityId);
    }

    [TestMethod]
    public void RepeatedRollsKeepOtherSlotsAndStayInEligiblePool()
    {
        Activities.Add(Activity(gameA));
        var originalOther = Slots[1];
        for (var i = 0; i < 100; i++)
        {
            var before = Slots[0].ActivityId;
            var eligible = Candidates().Select(a => a.Id).ToList();
            planner.RerollSlot(0);
            var after = Slots[0].ActivityId;
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
        Unique(unique);
        for (var i = 0; i < 100; i++)
        {
            planner.RandomizeAll();
            Assert.AreEqual(2, Slots.Count);
            Assert.AreEqual(2, Slots.Select(s => s.ActivityId).Distinct().Count());
            var otherSlot = Slots[1];
            planner.RerollSlot(0);
            Assert.AreSame(otherSlot, Slots[1]);
            Assert.AreEqual(2, Slots.Select(s => s.ActivityId).Distinct().Count());
            Assert.IsNull(planner.GenerationError);
        }
    }

    [TestMethod]
    public void SameGameCanFillAllSlotsWithoutDuplicateIds()
    {
        Unique(false);
        SetIds("_custGameIds", gameA);
        Activities.Add(current); // Duplicate source records are not extra capacity.
        for (var i = 0; i < 100; i++)
        {
            planner.RandomizeAll();
            Assert.AreEqual(2, Slots.Count);
            foreach (var s in Slots) Assert.AreEqual(gameA, s.GameId);
            Assert.AreEqual(2, Slots.Select(s => s.ActivityId).Distinct().Count());
        }
        planner.AddSlot();
        Assert.AreEqual(2, Slots.Count);
    }

    [TestMethod]
    [DataRow("_custGameIds")]
    [DataRow("_custThemeIds")]
    [DataRow("_custHolidayIds")]
    public void ImpossibleRandomizePreservesSlotsAndReportsError(string filter)
    {
        var original = Slots;
        SetIds(filter, Guid.NewGuid());
        planner.RandomizeAll();
        Assert.AreSame(original, Slots);
        Assert.AreEqual(2, Slots.Count);
        Assert.IsFalse(string.IsNullOrWhiteSpace(planner.GenerationError));
        SetIds(filter);
        planner.RandomizeAll();
        Assert.IsNull(planner.GenerationError);
    }

    [TestMethod]
    public void InitialImpossibleGenerationShowsErrorRatherThanSilentlyBlank()
    {
        planner.SetSlots([]);
        Activities.Clear();
        Activities.Add(Activity(Guid.NewGuid()));
        planner.RandomizeAll();
        Assert.IsEmpty(Slots);
        Assert.IsFalse(string.IsNullOrWhiteSpace(planner.GenerationError));
    }

    [TestMethod]
    public void RandomizeAppliesAllFiltersTogether()
    {
        Unique(false);
        current.ThemeIds = [theme];
        current.HolidayIds = [holiday];
        Activities.Add(Activity(gameA));
        SetIds("_custGameIds", gameA);
        SetIds("_custThemeIds", theme);
        SetIds("_custHolidayIds", holiday);
        planner.Filter = planner.Filter with { ThemedOnly = true };
        planner.RandomizeAll();
        Assert.AreEqual(2, Slots.Count);
        foreach (var s in Slots) Assert.Contains(s.ActivityId, new[] { current.Id, replacement.Id });
        Assert.AreEqual(2, Slots.Select(s => s.ActivityId).Distinct().Count());
        Assert.IsNull(planner.GenerationError);
    }

    [TestMethod]
    public void AddSlotSkipsExhaustedGames()
    {
        Unique(false);
        for (var i = 0; i < 100; i++)
        {
            planner.AddSlot();
            Assert.AreEqual(3, Slots.Count);
            Assert.AreEqual(replacement.Id, Slots[2].ActivityId);
            planner.SetSlots(Slots.Take(2));
        }
    }

    [TestMethod]
    public void UniqueRandomizeReassignsEarlierActivityInsteadOfFailing()
    {
        UseRandom(new FirstChoiceRandom());
        Unique(true);
        other.Id = current.Id;
        SetActivities([current, replacement, other]);
        var source = Activities.ToArray();
        planner.GenerationError = "Previous failure";

        for (var run = 0; run < 2; run++)
        {
            planner.RandomizeAll();
            Assert.IsNull(planner.GenerationError);
            Assert.AreEqual(2, Slots.Count);
            var selections = Slots.ToArray();
            Assert.AreEqual(replacement.Id, selections.Single(s => s.GameId == gameA).ActivityId);
            Assert.AreEqual(current.Id, selections.Single(s => s.GameId == gameB).ActivityId);
            Assert.AreSequenceEqual(source, Activities.ToArray());
        }
    }

    [TestMethod]
    public void UniqueRandomizeConsidersGamesBeyondAnUnmatchableInitialSubset()
    {
        var third = new GameEntity { Id = Guid.NewGuid() };
        games.Add(third);
        other.Id = current.Id;
        var thirdActivity = Activity(third.Id);
        SetActivities([current, other, thirdActivity]);
        UseRandom(new FirstChoiceRandom());
        planner.GenerationError = "Previous failure";

        planner.RandomizeAll();

        Assert.IsNull(planner.GenerationError);
        Assert.AreEqual(2, Slots.Count);
        var selections = Slots.ToArray();
        Assert.AreEqual(current.Id, selections.Single(s => s.GameId == gameA).ActivityId);
        Assert.AreEqual(thirdActivity.Id, selections.Single(s => s.GameId == third.Id).ActivityId);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ExhaustedGameIsUnavailableAndChoosingItPreservesSlotsAndError(bool unique)
    {
        Unique(unique);
        var exhausted = new GameEntity { Id = Guid.NewGuid() };
        games.Add(exhausted);
        Activities.Add(new ActivityEntity { GameId = exhausted.Id, Id = other.Id });
        const string error = "Previous generation failure";
        planner.GenerationError = error;
        var original = Slots;
        var originalSlots = Slots.ToArray();

        var available = planner.GetAvailableGames(0);
        Assert.DoesNotContain(g => g.Id == exhausted.Id, available);
        Assert.Contains(g => g.Id == gameA, available);
        Assert.DoesNotContain(a => a.GameId == exhausted.Id, Candidates());

        // Defend against a stale dropdown callback as well as filtering options.
        planner.ChangeSlotGame(0, exhausted);
        Assert.AreSame(original, Slots);
        Assert.AreSame(originalSlots[0], Slots[0]);
        Assert.AreSame(originalSlots[1], Slots[1]);
        Assert.AreEqual(error, planner.GenerationError);

        // Once a real assignment is available, change only this slot and clear the error.
        var unused = Activity(exhausted.Id);
        Activities.Add(unused);
        Assert.Contains(g => g.Id == exhausted.Id, planner.GetAvailableGames(0));
        planner.ChangeSlotGame(0, exhausted);
        Assert.AreEqual(exhausted.Id, Slots[0].GameId);
        Assert.AreEqual(unused.Id, Slots[0].ActivityId);
        Assert.AreSame(originalSlots[1], Slots[1]);
        Assert.AreEqual(2, Slots.Count);
        Assert.IsNull(planner.GenerationError);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RerollWithOnlyExhaustedGamesPreservesSlotsAndError(bool unique)
    {
        Unique(unique);
        Activities.Remove(replacement);
        var exhausted = new GameEntity { Id = Guid.NewGuid() };
        games.Add(exhausted);
        Activities.Add(new ActivityEntity { GameId = exhausted.Id, Id = other.Id });
        const string error = "Previous generation failure";
        planner.GenerationError = error;
        var original = Slots;
        var originalSlots = Slots.ToArray();

        Assert.IsEmpty(Candidates());
        planner.RerollSlot(0);

        Assert.AreSame(original, Slots);
        Assert.AreSame(originalSlots[0], Slots[0]);
        Assert.AreSame(originalSlots[1], Slots[1]);
        Assert.AreEqual(2, Slots.Count);
        Assert.AreEqual(error, planner.GenerationError);
    }

    [TestMethod]
    public void UniqueRandomizeMatchesSmallGraphFeasibility()
    {
        var graphGames = Enumerable.Range(1, 3).Select(_ => new GameEntity { Id = Guid.NewGuid() }).ToList();
        var ids = Enumerable.Range(1, 3).Select(_ => Guid.NewGuid()).ToArray();
        games = graphGames;
        for (var mask = 0; mask < 512; mask++)
        {
            var graph = new List<ActivityEntity>();
            for (var g = 0; g < 3; g++)
                for (var a = 0; a < 3; a++)
                    if ((mask & (1 << (g * 3 + a))) != 0)
                        graph.Add(new ActivityEntity { GameId = graphGames[g].Id, Id = ids[a] });
            activities = graph;
            for (var count = 1; count <= 3; count++)
            {
                // A planner per draw, seeded with the graph number, as the page test set _random.
                planner = NewPlanner(new Random(mask));
                Unique(true);
                planner.SetSlots(Enumerable.Repeat(Slot(current), count));
                var original = Slots;
                planner.RandomizeAll();
                var feasible = count <= Capacity(0, 0);
                Assert.IsTrue(feasible == (planner.GenerationError is null), $"Graph {mask}, count {count}");
                if (!feasible)
                {
                    Assert.AreSame(original, Slots);
                    continue;
                }
                var selections = Slots.ToArray();
                Assert.AreEqual(count, selections.Length);
                Assert.AreEqual(count, selections.Select(s => s.GameId).Distinct().Count());
                Assert.AreEqual(count, selections.Select(s => s.ActivityId).Distinct().Count());
                foreach (var slot in selections)
                    Assert.Contains(a => a.GameId == slot.GameId && a.Id == slot.ActivityId, graph);
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
        var chainGames = Enumerable.Range(1, 4).Select(_ => new GameEntity { Id = Guid.NewGuid() }).ToList();
        var ids = Enumerable.Range(1, 4).Select(_ => Guid.NewGuid()).ToArray();
        var chain = new List<ActivityEntity>();
        for (var i = 0; i < 3; i++)
        {
            chain.Add(new ActivityEntity { GameId = chainGames[i].Id, Id = ids[i] });
            chain.Add(new ActivityEntity { GameId = chainGames[i].Id, Id = ids[i + 1] });
        }
        chain.Add(new ActivityEntity { GameId = chainGames[3].Id, Id = ids[0] });
        SetGames(chainGames);
        SetActivities(chain);
        UseRandom(new FirstChoiceRandom());
        planner.SetSlots(chainGames.Select(game => Slot(Activity(game.Id))));
        planner.RandomizeAll();
        Assert.IsNull(planner.GenerationError);
        Assert.AreEqual(4, Slots.Count);
        for (var i = 0; i < 4; i++)
            Assert.AreEqual(ids[(i + 1) % 4], Slots.Single(s => s.GameId == chainGames[i].Id).ActivityId);
    }

    // The page rows of this test (ShowEditEvent, ShowCustomize) stay in SlotRerollTests.
    [TestMethod]
    [DataRow("RandomizeAll", true)]
    [DataRow("RandomizeAll", false)]
    [DataRow("RerollSlot", true)]
    [DataRow("ChangeSlotGame", true)]
    [DataRow("ChangeSlotActivity", true)]
    [DataRow("AddSlot", false)]
    [DataRow("RemoveSlot", true)]
    public void SuccessfulSelectionMutationClearsStaleGenerationError(string handler, bool unique)
    {
        UseRandom(new FirstChoiceRandom());
        Unique(unique);
        planner.GenerationError = "Previous generation failure";
        if (handler == "RemoveSlot") AddSlot(replacement);

        switch (handler)
        {
            case "RerollSlot":
                planner.RerollSlot(0);
                Assert.AreEqual(replacement.Id, Slots[0].ActivityId);
                break;
            case "ChangeSlotGame":
                var newGame = new GameEntity { Id = Guid.NewGuid() };
                var newActivity = Activity(newGame.Id);
                games.Add(newGame);
                Activities.Add(newActivity);
                planner.ChangeSlotGame(0, newGame);
                Assert.AreEqual(newGame.Id, Slots[0].GameId);
                Assert.AreEqual(newActivity.Id, Slots[0].ActivityId);
                break;
            case "ChangeSlotActivity":
                planner.ChangeSlotActivity(0, replacement);
                Assert.AreEqual(replacement.Id, Slots[0].ActivityId);
                break;
            default:
                if (handler == "RemoveSlot") planner.RemoveSlot(2);
                else if (handler == "AddSlot") planner.AddSlot();
                else planner.RandomizeAll();
                Assert.AreEqual(handler is "AddSlot" ? 3 : 2, Slots.Count);
                break;
        }
        Assert.IsNull(planner.GenerationError);
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
        planner.GenerationError = error;
        if (handler == "RerollSlot") Activities.Remove(replacement);
        if (handler == "FullAddSlot")
            while (Slots.Count < 5) AddSlot(replacement);
        // RemoveSlot keeps a floor of one slot, so the no-op case needs exactly one slot.
        if (handler == "RemoveSlot") planner.SetSlots(Slots.Take(1));
        var original = Slots;
        var originalSlots = Slots.ToArray();

        switch (handler)
        {
            case "RerollSlot": planner.RerollSlot(0); break;
            case "RemoveSlot": planner.RemoveSlot(0); break;
            case "InvalidReroll": planner.RerollSlot(-1); break;
            case "ChangeSlotGame": planner.ChangeSlotGame(0, null); break;
            case "ChangeSlotActivity": planner.ChangeSlotActivity(0, null); break;
            default: planner.AddSlot(); break;
        }

        Assert.AreEqual(error, planner.GenerationError);
        Assert.AreSame(original, Slots);
        Assert.AreSequenceEqual(originalSlots, Slots.ToArray());
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void FailedRandomizeKeepsSelectionsAndErrorUntilSuccessfulRetry(bool unique)
    {
        UseRandom(new FirstChoiceRandom());
        Unique(unique);
        SetIds("_custGameIds", gameB);
        var original = Slots;
        var originalSlots = Slots.ToArray();
        planner.RandomizeAll();
        var error = planner.GenerationError;
        Assert.IsNotNull(error);
        planner.RandomizeAll();
        Assert.AreEqual(error, planner.GenerationError);
        Assert.AreSame(original, Slots);
        Assert.AreSequenceEqual(originalSlots, Slots.ToArray());

        SetIds("_custGameIds");
        planner.RandomizeAll();
        Assert.IsNull(planner.GenerationError);
        Assert.AreEqual(2, Slots.Count);
    }

    [TestMethod]
    public void RemoveAllowsOneSlotButNeverRemovesLastSlot()
    {
        planner.RemoveSlot(1);
        Assert.ContainsSingle(Slots);
        planner.RemoveSlot(0);
        Assert.ContainsSingle(Slots);
        Assert.AreEqual(current.Id, Slots[0].ActivityId);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void OneSlotRandomizationAndRerollRespectFilters(bool unique)
    {
        planner.RemoveSlot(1);
        Unique(unique);
        SetIds("_custGameIds", gameA);
        SetIds("_custThemeIds", theme);
        SetIds("_custHolidayIds", holiday);
        planner.Filter = planner.Filter with { ThemedOnly = true };
        planner.RandomizeAll();
        Assert.ContainsSingle(Slots);
        Assert.AreEqual(replacement.Id, Slots[0].ActivityId);
        Assert.IsEmpty(Candidates());
    }

    [TestMethod]
    [DataRow(1, 1)]
    [DataRow(2, 2)]
    [DataRow(5, 3)]
    public void FreshRandomizeWithRepeatedGamesAllowedSizesSlotsToTheActivities(int activityCount, int expectedSlots)
    {
        UseLibrary(activityCount);
        planner.SetSlots([]);
        Unique(false);
        planner.RandomizeAll();
        AssertFilledSlots(expectedSlots, distinctGames: false);
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
        planner.SetSlots([]);
        Unique(unique);
        SetIds(filter, Guid.NewGuid());
        planner.RandomizeAll();
        Assert.IsEmpty(Slots);
        Assert.AreEqual(NoMatchMessage, planner.GenerationError);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RandomizeAllWithExistingSelectionsKeepsThemAndSaysSo(bool unique)
    {
        var library = UseLibrary(1, 1, 1);
        SetSlots(Activities.ToArray());
        Unique(unique);
        SetIds("_custGameIds", library[0].Id, library[1].Id);
        var original = Slots;
        var originalSlots = Slots.ToArray();
        planner.RandomizeAll();
        Assert.AreSame(original, Slots);
        Assert.AreSequenceEqual(originalSlots, Slots.ToArray());
        Assert.Contains("Existing selections were kept", planner.GenerationError!);
    }

    [TestMethod]
    public void UniqueGamesBlocksAddWhenOnlyUsedGamesHaveFreeActivities()
    {
        var library = UseLibrary(2, 1, 1);
        var free = Activities[1];
        SetSlots(Activities[0], Activities[2], Activities[3]);
        Unique(true);
        Assert.IsEmpty(planner.GetAddSlotCandidates());
        AssertAddSlotRefused("Every Game with an unused activity is already in a slot. Turn off Unique games only to repeat a Game.");

        Unique(false);
        Assert.IsNull(planner.GetAddSlotBlockedReason());
        Assert.AreEqual(free.Id, Assert.ContainsSingle(planner.GetAddSlotCandidates()).Id);
        planner.AddSlot();
        Assert.AreEqual(4, Slots.Count);
        Assert.AreEqual(free.Id, Slots[3].ActivityId);
        Assert.AreEqual(library[0].Id, Slots[3].GameId);
        Assert.IsNull(planner.AddSlotError);
        Assert.AreEqual(4, Slots.Select(s => s.ActivityId).Distinct().Count());
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
        var library = UseLibrary(1, 1, 1, 1);
        SetSlots(Activities[0], Activities[1], Activities[2]);
        Unique(unique);
        SetIds(filter, Guid.NewGuid());
        Assert.IsEmpty(planner.GetAddSlotCandidates());
        AssertAddSlotRefused(AddSlotNoMatchReason);

        // Clearing the filter frees the fourth Game: one click adds exactly that one slot.
        SetIds(filter);
        Assert.IsNull(planner.GetAddSlotBlockedReason());
        planner.AddSlot();
        Assert.AreEqual(4, Slots.Count);
        Assert.AreEqual(Activities[3].Id, Slots[3].ActivityId);
        Assert.AreEqual(library[3].Id, Slots[3].GameId);
        Assert.IsNull(planner.AddSlotError);
        Assert.IsNull(planner.GenerationError);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void AddSlotAddsExactlyOneUniqueSlotPerClickUntilTheLimit(bool unique)
    {
        UseLibrary(2, 2, 2, 2, 2, 2);
        Unique(unique);
        for (var run = 0; run < 50; run++)
        {
            SetSlots(Activities[0]);
            for (var expected = 2; expected <= EventEntity.MaximumSelections; expected++)
            {
                Assert.IsNull(planner.GetAddSlotBlockedReason());
                planner.AddSlot();
                Assert.AreEqual(expected, Slots.Count);
                var slots = Slots.ToArray();
                Assert.AreEqual(expected, slots.Select(s => s.ActivityId).Distinct().Count());
                if (unique) Assert.AreEqual(expected, slots.Select(s => s.GameId).Distinct().Count());
                foreach (var s in slots) Assert.Contains(a => a.Id == s.ActivityId && a.GameId == s.GameId, Activities);
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
            Unique(unique);
            var rerollPool = Candidates().Select(a => a.Id).ToHashSet();
            var all = Slots.ToList();
            planner.SetSlots(all.Skip(1));
            var addPool = planner.GetAddSlotCandidates().Select(a => a.Id).ToHashSet();
            planner.SetSlots(all);
            addPool.Remove(current.Id);
            CollectionAssert.AreEquivalent(addPool.ToList(), rerollPool.ToList());
        }
    }

    // Scenarios carried over from the API generator tests that #48 removed: the planner is the
    // only place that draws slots, so it must keep these rules.

    [TestMethod]
    public void RepeatedGamesExhaustPoolWithoutRepeatingIds()
    {
        var library = UseLibrary(5);
        Activities.Add(Activities[0]); // Duplicate records do not add capacity.
        var source = Activities.ToArray();
        Unique(false);
        for (var i = 0; i < 100; i++)
        {
            SetSlots(Enumerable.Repeat(Activities[0], 5).ToArray());
            planner.RandomizeAll();
            AssertFilledSlots(5, distinctGames: false);
            foreach (var s in Slots) Assert.AreEqual(library[0].Id, s.GameId);
        }
        Assert.AreSequenceEqual(source, Activities.ToArray());

        // Four distinct ids plus the duplicate cannot fill five slots.
        Activities.Remove(source[4]);
        var original = Slots;
        var originalSlots = Slots.ToArray();
        planner.RandomizeAll();
        Assert.AreSame(original, Slots);
        Assert.AreSequenceEqual(originalSlots, Slots.ToArray());
        Assert.IsNotNull(planner.GenerationError);

        // One Game cannot fill two slots with Unique games only on.
        SetSlots(source[0], source[1]);
        Unique(true);
        planner.GenerationError = null;
        original = Slots;
        planner.RandomizeAll();
        Assert.AreSame(original, Slots);
        Assert.IsNotNull(planner.GenerationError);
    }

    [TestMethod]
    public void SkewedPoolAlwaysFillsEverySlot()
    {
        // One Game holds almost every Activity; four Games hold one each.
        UseLibrary(50, 1, 1, 1, 1);
        Unique(false);
        for (var i = 0; i < 100; i++)
        {
            SetSlots(Enumerable.Repeat(Activities[0], 5).ToArray());
            planner.RandomizeAll();
            AssertFilledSlots(5, distinctGames: false);
            foreach (var s in Slots)
                Assert.Contains(a => a.Id == s.ActivityId && a.GameId == s.GameId, Activities);
        }
    }

    [TestMethod]
    public void SkewedPoolExhaustsEveryDistinctIdWithoutMutatingInputs()
    {
        // Five distinct ids, two of them on one Game, plus duplicate records of that Game's ids.
        var library = UseLibrary(2, 1, 1, 1);
        var expected = Activities.Select(a => a.Id).ToArray();
        Activities.AddRange(Activities.Take(2).ToArray());
        var source = Activities.ToArray();
        var originalGames = library.ToArray();
        Unique(false);

        // Reuse the same planner and inputs to catch state leaking between draws.
        for (var run = 0; run < 50; run++)
        {
            SetSlots(Enumerable.Repeat(Activities[0], 5).ToArray());
            planner.RandomizeAll();
            AssertFilledSlots(5, distinctGames: false);
            CollectionAssert.AreEquivalent(expected, Slots.Select(s => s.ActivityId).ToArray());
            foreach (var s in Slots)
                Assert.Contains(a => a.Id == s.ActivityId && a.GameId == s.GameId, Activities);
            Assert.AreSequenceEqual(source, Activities.ToArray());
            Assert.AreSequenceEqual(originalGames, games.ToArray());
        }
    }

    [TestMethod]
    public void DuplicateIdsAcrossPoolsAreRemovedTogetherIncludingExhaustedGames()
    {
        var library = UseLibrary(Enumerable.Repeat(0, 10).ToArray());
        var (one, two, three) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        // Ids one and two appear in every Game (one twice per Game); id three only in the first.
        var pool = library.SelectMany(g => new[]
        {
            new ActivityEntity { Id = one, GameId = g.Id },
            new ActivityEntity { Id = one, GameId = g.Id },
            new ActivityEntity { Id = two, GameId = g.Id },
        }).ToList();
        pool.Add(new ActivityEntity { Id = three, GameId = library[0].Id });
        SetActivities(pool);
        Unique(false);

        for (var run = 0; run < 20; run++)
        {
            SetSlots(pool[0], pool[0], pool[0]);
            planner.RandomizeAll();
            AssertFilledSlots(3, distinctGames: false);
            CollectionAssert.AreEquivalent(new[] { one, two, three }, Slots.Select(s => s.ActivityId).ToArray());
            foreach (var s in Slots)
                Assert.Contains(a => a.Id == s.ActivityId && a.GameId == s.GameId, pool);
        }

        // Three distinct ids cannot fill four slots, however many Games repeat them.
        SetSlots(pool[0], pool[0], pool[0], pool[0]);
        var original = Slots;
        planner.RandomizeAll();
        Assert.AreSame(original, Slots);
        Assert.IsNotNull(planner.GenerationError);
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
        SetActivities([eligible, wrongGame, orphanActivity, themeOnly, holidayOnly, unthemed]);
        Unique(unique);
        planner.Filter = planner.Filter with { ThemedOnly = true };
        SetIds("_custGameIds", gameA, orphan);
        SetIds("_custThemeIds", theme);
        SetIds("_custHolidayIds", holiday);

        SetSlots(current);
        planner.RandomizeAll();
        AssertFilledSlots(1, distinctGames: true);
        Assert.AreEqual(eligible.Id, Slots[0].ActivityId);
        Assert.AreEqual(gameA, Slots[0].GameId);

        // The orphan's Activity must not add capacity ...
        SetSlots(current, other);
        var original = Slots;
        planner.RandomizeAll();
        Assert.AreSame(original, Slots);
        Assert.IsNotNull(planner.GenerationError);

        // ... and no Games means no capacity at all.
        var library = games;
        SetGames([]);
        SetSlots(current);
        original = Slots;
        planner.RandomizeAll();
        Assert.AreSame(original, Slots);
        Assert.IsNotNull(planner.GenerationError);
        SetGames(library);

        // Dropping the Theme filter admits the holiday-only Activity (themed via its Holiday).
        SetActivities([holidayOnly]);
        SetIds("_custThemeIds");
        planner.RandomizeAll();
        AssertFilledSlots(1, distinctGames: true);
        Assert.AreEqual(holidayOnly.Id, Slots[0].ActivityId);

        // An unthemed Activity stays excluded while Themed activities only is on ...
        SetActivities([unthemed]);
        SetIds("_custHolidayIds");
        original = Slots;
        planner.RandomizeAll();
        Assert.AreSame(original, Slots);
        Assert.IsNotNull(planner.GenerationError);

        // ... and is admitted once every Filter is cleared.
        planner.Filter = planner.Filter with { ThemedOnly = false };
        SetIds("_custGameIds");
        planner.RandomizeAll();
        AssertFilledSlots(1, distinctGames: true);
        Assert.AreEqual(unthemed.Id, Slots[0].ActivityId);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RandomizeAllAcceptsUpperBoundaryWithoutRepeatingActivities(bool unique)
    {
        // With repeated Games allowed, use one Game so all five slots must share it.
        UseLibrary(unique ? new[] { 1, 1, 1, 1, 1 } : new[] { 5 });
        Unique(unique);
        for (var i = 0; i < 100; i++)
        {
            SetSlots(Enumerable.Repeat(Activities[0], EventEntity.MaximumSelections).ToArray());
            planner.RandomizeAll();
            AssertFilledSlots(EventEntity.MaximumSelections, distinctGames: unique);
        }
    }

    // Added with the extraction: until both library lists have loaded, nothing can be picked.
    [TestMethod]
    public void LibraryNotLoadedYetOffersNothingAndRandomizeReportsNoMatch()
    {
        var unloaded = new (List<GameEntity>? Games, List<ActivityEntity>? Activities)[] { (games, null), (null, activities), (null, null) };
        foreach (var (g, a) in unloaded)
        {
            planner.SetLibrary(g, a);
            Assert.IsEmpty(Candidates());
            Assert.IsEmpty(planner.GetAddSlotCandidates());
            Assert.IsEmpty(planner.GetAvailableGames(0));
            Assert.IsEmpty(planner.GetAvailableActivities(gameA, 0));
            Assert.AreEqual(AddSlotNoMatchReason, planner.GetAddSlotBlockedReason());
        }
        planner.SetSlots([]);
        planner.RandomizeAll();
        Assert.IsEmpty(Slots);
        Assert.AreEqual(NoMatchMessage, planner.GenerationError);
    }

    private const string NoMatchMessage = "No activities match the current filters. Adjust the filters or add activities to the library.";
    private const string AddSlotNoMatchReason = "No unused activity matches the current filters. Adjust the filters or add activities to the library.";

    // Stable sort keys and first-choice draws force the greedy dead end.
    private sealed class FirstChoiceRandom : Random
    {
        public override int Next() => 0;
        public override int Next(int maxValue) => 0;
    }

    private static ActivityEntity Activity(Guid game) => new() { Id = Guid.NewGuid(), GameId = game };

    private static SlotSelection Slot(ActivityEntity activity) => new() { GameId = activity.GameId, ActivityId = activity.Id };

    private IReadOnlyList<SlotSelection> Slots => planner.Slots;

    // The library the planner reads. Tests change it in place, as the page tests changed the page's lists.
    private List<ActivityEntity> Activities => activities;

    private List<ActivityEntity> Candidates() => planner.GetRerollCandidates(0);

    private SlotPlanner NewPlanner(Random random)
    {
        var p = new SlotPlanner(random);
        p.SetLibrary(games, activities);
        return p;
    }

    // Swaps in a planner that rolls with random, keeping the library, filters, slots and errors.
    private void UseRandom(Random random)
    {
        var p = NewPlanner(random);
        p.Filter = planner.Filter;
        p.SetSlots(planner.Slots);
        p.GenerationError = planner.GenerationError;
        p.AddSlotError = planner.AddSlotError;
        planner = p;
    }

    private void SetGames(List<GameEntity> library)
    {
        games = library;
        planner.SetLibrary(games, activities);
    }

    private void SetActivities(List<ActivityEntity> library)
    {
        activities = library;
        planner.SetLibrary(games, activities);
    }

    private void Unique(bool on) => planner.Filter = planner.Filter with { UniqueGames = on };

    // Sets one id filter by the page field it is bound to there; the names keep the moved tests' data rows.
    private void SetIds(string field, params Guid[] ids) => planner.Filter = field switch
    {
        "_custGameIds" => planner.Filter with { GameIds = ids },
        "_custThemeIds" => planner.Filter with { ThemeIds = ids },
        "_custHolidayIds" => planner.Filter with { HolidayIds = ids },
        _ => throw new ArgumentOutOfRangeException(nameof(field), field)
    };

    private void AddSlot(ActivityEntity activity) => planner.SetSlots([.. planner.Slots, Slot(activity)]);

    private void SetSlots(params ActivityEntity[] slots) => planner.SetSlots(slots.Select(Slot));

    // Replaces the fixture library with one game per entry, holding that many activities each.
    private List<GameEntity> UseLibrary(params int[] activitiesPerGame)
    {
        var library = new List<GameEntity>();
        var pool = new List<ActivityEntity>();
        foreach (var count in activitiesPerGame)
        {
            var game = new GameEntity { Id = Guid.NewGuid() };
            library.Add(game);
            for (var i = 0; i < count; i++) pool.Add(Activity(game.Id));
        }
        games = library;
        SetActivities(pool);
        return library;
    }

    private void AssertFilledSlots(int expected, bool distinctGames)
    {
        Assert.AreEqual(expected, Slots.Count);
        Assert.IsNull(planner.GenerationError);
        Assert.AreEqual(expected, Slots.Select(s => s.ActivityId).Distinct().Count());
        if (distinctGames) Assert.AreEqual(expected, Slots.Select(s => s.GameId).Distinct().Count());
    }

    // A blocked Add Activity Slot keeps every slot, keeps the generation error and says why.
    private void AssertAddSlotRefused(string expectedReason)
    {
        const string error = "Previous generation failure";
        planner.GenerationError = error;
        var original = Slots;
        var originalSlots = Slots.ToArray();
        Assert.AreEqual(expectedReason, planner.GetAddSlotBlockedReason());

        planner.AddSlot();

        Assert.AreSame(original, Slots);
        Assert.AreSequenceEqual(originalSlots, Slots.ToArray());
        Assert.AreEqual(expectedReason, planner.AddSlotError);
        Assert.AreEqual(error, planner.GenerationError);
    }
}
