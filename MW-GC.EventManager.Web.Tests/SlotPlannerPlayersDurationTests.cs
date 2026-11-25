using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Tests;

// #98 part 2: the players tonight and longest duration Filters, driven through SlotPlanner's public API with the
// players number and the duration limit as plain inputs. Each roll path (Randomize All, re-roll, Add Activity Slot)
// and the slot pickers' lists must honour both, and an Activity with no values always passes.
[TestClass]
public sealed class SlotPlannerPlayersDurationTests
{
    private const string NoMatch = "No activities match the current filters. Adjust the filters or add activities to the library.";
    private const string SelectionsKept = "Not enough distinct activities or games match the current filters. Existing selections were kept.";
    private const string AddSlotNoMatch = "No unused activity matches the current filters. Adjust the filters or add activities to the library.";

    private readonly List<GameEntity> games = [];
    private readonly List<ActivityEntity> activities = [];

    // ---- the matching rules --------------------------------------------------------------------

    [TestMethod]
    [DataRow(3, null, 2, false, DisplayName = "min only, N below")]
    [DataRow(3, null, 3, true, DisplayName = "min only, N at")]
    [DataRow(3, null, 4, true, DisplayName = "min only, N above")]
    [DataRow(null, 6, 5, true, DisplayName = "max only, N below")]
    [DataRow(null, 6, 6, true, DisplayName = "max only, N at")]
    [DataRow(null, 6, 7, false, DisplayName = "max only, N above")]
    [DataRow(2, 6, 1, false, DisplayName = "both, N below min")]
    [DataRow(2, 6, 2, true, DisplayName = "both, N at min")]
    [DataRow(2, 6, 4, true, DisplayName = "both, N between")]
    [DataRow(2, 6, 6, true, DisplayName = "both, N at max")]
    [DataRow(2, 6, 7, false, DisplayName = "both, N above max")]
    [DataRow(null, null, 1, true, DisplayName = "neither, N 1")]
    [DataRow(null, null, 99, true, DisplayName = "neither, N 99")]
    [DataRow(4, 4, 3, false, DisplayName = "equal bounds, N below")]
    [DataRow(4, 4, 4, true, DisplayName = "equal bounds, N at")]
    [DataRow(4, 4, 5, false, DisplayName = "equal bounds, N above")]
    public void PlayersTonight_KeepsAnActivityUnlessABoundExcludesThatNumber(int? min, int? max, int players, bool kept)
    {
        var activity = Add(min: min, max: max);
        var planner = Planner(SlotFilter.Default with { PlayersTonight = players });

        Assert.AreEqual(kept, planner.GetAddSlotCandidates().Contains(activity));
    }

    [TestMethod]
    [DataRow(null, 30, true, DisplayName = "no duration")]
    [DataRow(29, 30, true, DisplayName = "under the limit")]
    [DataRow(30, 30, true, DisplayName = "at the limit")]
    [DataRow(31, 30, false, DisplayName = "one over the limit")]
    [DataRow(1440, 1440, true, DisplayName = "at the largest limit")]
    [DataRow(2, 1, false, DisplayName = "one over the smallest limit")]
    public void LongestDuration_KeepsAnActivityWithNoDurationOrOneWithinTheLimit(int? duration, int limit, bool kept)
    {
        var activity = Add(duration: duration);
        var planner = Planner(SlotFilter.Default with { LongestDuration = limit });

        Assert.AreEqual(kept, planner.GetAddSlotCandidates().Contains(activity));
    }

    [TestMethod]
    [DataRow(1, null)]
    [DataRow(99, null)]
    [DataRow(null, 1)]
    [DataRow(1, 1)]
    [DataRow(99, 1440)]
    public void AnActivityWithNoValues_AlwaysPasses(int? players, int? limit)
    {
        var plain = Add();
        var planner = Planner(SlotFilter.Default with { PlayersTonight = players, LongestDuration = limit });

        Assert.AreSequenceEqual(new[] { plain.Id }, planner.GetAddSlotCandidates().Select(a => a.Id));
    }

    // ---- every roll path honours both ----------------------------------------------------------

    // The excluded Activity's Game comes first, so the first-choice draws pick it whenever the Filter lets it through.
    [TestMethod]
    [DataRow("players", true)]
    [DataRow("players", false)]
    [DataRow("duration", true)]
    [DataRow("duration", false)]
    public void RandomizeAll_NeverPicksAnActivityTheFilterExcludes(string filter, bool uniqueGames)
    {
        Excluded(filter);
        var kept = Kept(filter);
        var planner = Planner(On(filter) with { UniqueGames = uniqueGames });

        planner.RandomizeAll();

        Assert.AreSequenceEqual(new[] { kept.Id }, planner.Slots.Select(s => s.ActivityId));
        Assert.IsNull(planner.GenerationError);
    }

    [TestMethod]
    [DataRow("players")]
    [DataRow("duration")]
    public void Reroll_NeverPicksAnActivityTheFilterExcludes(string filter)
    {
        var slot = Add();
        Excluded(filter);
        var kept = Kept(filter);
        var planner = Planner(On(filter), slot);

        Assert.AreSequenceEqual(new[] { kept.Id }, planner.GetRerollCandidates(0).Select(a => a.Id));
        planner.RerollSlot(0);

        Assert.AreEqual(kept.Id, planner.Slots[0].ActivityId);
    }

    [TestMethod]
    [DataRow("players")]
    [DataRow("duration")]
    public void AddActivitySlot_NeverPicksAnActivityTheFilterExcludes(string filter)
    {
        var slot = Add();
        Excluded(filter);
        var kept = Kept(filter);
        var planner = Planner(On(filter), slot);

        Assert.AreSequenceEqual(new[] { kept.Id }, planner.GetAddSlotCandidates().Select(a => a.Id));
        planner.AddSlot();

        Assert.AreSequenceEqual(new[] { slot.Id, kept.Id }, planner.Slots.Select(s => s.ActivityId));
    }

    // Changing a slot by hand is restricted as the other Filters restrict it: the pickers offer only what passes.
    [TestMethod]
    [DataRow("players")]
    [DataRow("duration")]
    public void ThePickers_OfferOnlyWhatTheFilterKeeps(string filter)
    {
        var slot = Add();
        var excluded = Excluded(filter);
        var kept = Kept(filter);
        var planner = Planner(On(filter), slot);

        Assert.AreSequenceEqual(new[] { slot.GameId, kept.GameId }, planner.GetAvailableGames(0).Select(g => g.Id));
        Assert.IsEmpty(planner.GetAvailableActivities(excluded.GameId, 0));
        Assert.AreSequenceEqual(new[] { kept.Id }, planner.GetAvailableActivities(kept.GameId, 0).Select(a => a.Id));

        planner.ChangeSlotGame(0, games.Single(g => g.Id == excluded.GameId));
        Assert.AreEqual(slot.Id, planner.Slots[0].ActivityId);
    }

    // ---- no candidates -------------------------------------------------------------------------

    [TestMethod]
    [DataRow("players")]
    [DataRow("duration")]
    public void AFilterThatEmptiesThePool_ReusesTheNoMatchMessages_AndKeepsTheSelections(string filter)
    {
        var first = Excluded(filter);
        Excluded(filter);

        var fresh = Planner(On(filter));
        fresh.RandomizeAll();
        Assert.IsEmpty(fresh.Slots);
        Assert.AreEqual(NoMatch, fresh.GenerationError);

        var planner = Planner(On(filter), first);
        var before = planner.Slots.ToArray();
        planner.RandomizeAll();
        Assert.AreEqual(SelectionsKept, planner.GenerationError);
        Assert.AreSequenceEqual(before, planner.Slots.ToArray());

        Assert.IsEmpty(planner.GetRerollCandidates(0));
        planner.AddSlot();
        Assert.AreEqual(AddSlotNoMatch, planner.AddSlotError);
        Assert.AreSequenceEqual(before, planner.Slots.ToArray());
    }

    // ---- together ------------------------------------------------------------------------------

    [TestMethod]
    public void BothFiltersAndThemedOnly_Combine()
    {
        var theme = Guid.NewGuid();
        var fits = Add(min: 2, max: 6, duration: 30);
        fits.ThemeIds = [theme];
        var tooFewPlayers = Add(min: 5, duration: 30);
        tooFewPlayers.ThemeIds = [theme];
        var tooLong = Add(max: 6, duration: 31);
        tooLong.ThemeIds = [theme];
        Add(min: 2, max: 6, duration: 30); // fits both, but has no Theme or Holiday
        var planner = Planner(SlotFilter.Default with { ThemedOnly = true, PlayersTonight = 4, LongestDuration = 30 });

        Assert.AreSequenceEqual(new[] { fits.Id }, planner.GetAddSlotCandidates().Select(a => a.Id));
    }

    // ---- defaults ------------------------------------------------------------------------------

    [TestMethod]
    public void TheDefaultFilter_HasBothOff()
    {
        Assert.IsNull(SlotFilter.Default.PlayersTonight);
        Assert.IsNull(SlotFilter.Default.LongestDuration);
    }

    // The same seed rolls the same slots whether or not the Activities carry players and duration.
    [TestMethod]
    public void ARollWithTheDefaultFilter_IgnoresPlayersAndDuration()
    {
        for (var g = 0; g < 4; g++)
        {
            var game = Game();
            Add(min: 50, max: 60, game: game);
            Add(duration: 1440, game: game);
            Add(min: 1, max: 1, duration: 1, game: game);
        }
        var plainGames = games.ToList();
        var plain = activities.Select(a => new ActivityEntity { Id = a.Id, GameId = a.GameId }).ToList();

        for (var seed = 0; seed < 20; seed++)
        {
            var withValues = new SlotPlanner(new Random(seed));
            withValues.SetLibrary(games, activities);
            var without = new SlotPlanner(new Random(seed));
            without.SetLibrary(plainGames, plain);

            withValues.RandomizeAll();
            without.RandomizeAll();

            Assert.HasCount(3, without.Slots);
            Assert.AreSequenceEqual(without.Slots.Select(s => s.ActivityId).ToArray(), withValues.Slots.Select(s => s.ActivityId).ToArray());
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    // Stable sort keys and first-choice draws: a roll takes the first candidate it may.
    private sealed class FirstChoiceRandom : Random
    {
        public override int Next() => 0;
        public override int Next(int maxValue) => 0;
    }

    private static SlotFilter On(string filter) => filter switch
    {
        "players" => SlotFilter.Default with { PlayersTonight = 4 },
        "duration" => SlotFilter.Default with { LongestDuration = 30 },
        _ => throw new ArgumentOutOfRangeException(nameof(filter), filter)
    };

    // An Activity on a new Game that the Filter of On(filter) excludes, and one it keeps.
    private ActivityEntity Excluded(string filter) => filter == "players" ? Add(min: 5) : Add(duration: 31);
    private ActivityEntity Kept(string filter) => filter == "players" ? Add(min: 2, max: 4) : Add(duration: 30);

    private Guid Game()
    {
        var game = new GameEntity { Id = Guid.NewGuid() };
        games.Add(game);
        return game.Id;
    }

    // Adds an Activity to the library, on a new Game unless one is given.
    private ActivityEntity Add(int? min = null, int? max = null, int? duration = null, Guid? game = null)
    {
        var activity = new ActivityEntity { Id = Guid.NewGuid(), GameId = game ?? Game(), MinPlayers = min, MaxPlayers = max, DurationMinutes = duration };
        activities.Add(activity);
        return activity;
    }

    private SlotPlanner Planner(SlotFilter filter, params ActivityEntity[] slots)
    {
        var planner = new SlotPlanner(new FirstChoiceRandom()) { Filter = filter };
        planner.SetLibrary(games, activities);
        planner.SetSlots(slots.Select(a => new SlotSelection { GameId = a.GameId, ActivityId = a.Id }));
        return planner;
    }
}
