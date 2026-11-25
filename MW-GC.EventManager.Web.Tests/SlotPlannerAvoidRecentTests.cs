using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Tests;

// #95: the avoid-recent Filter, driven through SlotPlanner's public API with the recent Activity ids as plain input.
// Randomize All, re-roll and Add Activity Slot skip them; a slot changed by hand is not restricted. When the Filter
// leaves no candidate the message names it, and only then.
[TestClass]
public sealed class SlotPlannerAvoidRecentTests
{
    private const string NoMatch = "No activities match the current filters. Adjust the filters or add activities to the library.";
    private const string SelectionsKept = "Not enough distinct activities or games match the current filters. Existing selections were kept.";
    private const string AddSlotNoMatch = "No unused activity matches the current filters. Adjust the filters or add activities to the library.";
    private const string RerollNoMatch = "No alternative matches the current filters";
    private const string Advice = "Lower the number of recent Events to avoid, or turn Avoid recent off.";

    private readonly List<GameEntity> games = [];
    private readonly List<ActivityEntity> activities = [];

    // ---- every roll path honours it ------------------------------------------------------------

    // The recent Activity's Game comes first, so the first-choice draws pick it whenever the Filter lets it through.
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RandomizeAll_NeverPicksARecentActivity(bool uniqueGames)
    {
        var recent = Add();
        var kept = Add();
        var planner = Planner(Avoid(recent) with { UniqueGames = uniqueGames });

        planner.RandomizeAll();

        Assert.AreSequenceEqual(new[] { kept.Id }, planner.Slots.Select(s => s.ActivityId));
        Assert.IsNull(planner.GenerationError);
    }

    // Many seeds, a library of several Games: no roll ever offers an Activity of a recent Event.
    [TestMethod]
    public void RandomizeAll_WithManySeeds_NeverPicksARecentActivity()
    {
        for (var g = 0; g < 5; g++)
        {
            var game = Game();
            Add(game: game);
            Add(game: game);
        }
        var recent = activities.Where((_, i) => i % 2 == 0).Select(a => a.Id).ToHashSet();
        for (var seed = 0; seed < 30; seed++)
        {
            var planner = new SlotPlanner(new Random(seed)) { Filter = SlotFilter.Default with { RecentActivityIds = recent } };
            planner.SetLibrary(games, activities);

            planner.RandomizeAll();

            Assert.HasCount(3, planner.Slots);
            Assert.IsFalse(planner.Slots.Any(s => recent.Contains(s.ActivityId)), $"seed {seed} picked a recent Activity");
        }
    }

    [TestMethod]
    public void Reroll_NeverPicksARecentActivity()
    {
        var slot = Add();
        var recent = Add();
        var kept = Add();
        var planner = Planner(Avoid(recent), slot);

        Assert.AreSequenceEqual(new[] { kept.Id }, planner.GetRerollCandidates(0).Select(a => a.Id));
        planner.RerollSlot(0);

        Assert.AreEqual(kept.Id, planner.Slots[0].ActivityId);
        Assert.IsNull(planner.GetRerollBlockedReason(0));
    }

    [TestMethod]
    public void AddActivitySlot_NeverPicksARecentActivity()
    {
        var slot = Add();
        var recent = Add();
        var kept = Add();
        var planner = Planner(Avoid(recent), slot);

        Assert.AreSequenceEqual(new[] { kept.Id }, planner.GetAddSlotCandidates().Select(a => a.Id));
        planner.AddSlot();

        Assert.AreSequenceEqual(new[] { slot.Id, kept.Id }, planner.Slots.Select(s => s.ActivityId));
    }

    // Games are never skipped: an Activity of the same Game as a recent one is still offered.
    [TestMethod]
    public void AnotherActivityOfARecentActivitysGame_IsStillOffered()
    {
        var game = Game();
        var recent = Add(game: game);
        var sibling = Add(game: game);
        var planner = Planner(Avoid(recent));

        Assert.AreSequenceEqual(new[] { sibling.Id }, planner.GetAddSlotCandidates().Select(a => a.Id));
    }

    // A slot changed by hand is not restricted (#203): the pickers still offer a recent Activity and its Game.
    [TestMethod]
    public void ThePickers_StillOfferRecentActivities()
    {
        var slot = Add();
        var recent = Add();
        var planner = Planner(Avoid(recent), slot);

        Assert.AreSequenceEqual(new[] { slot.GameId, recent.GameId }, planner.GetAvailableGames(0).Select(g => g.Id));
        Assert.AreSequenceEqual(new[] { recent.Id }, planner.GetAvailableActivities(recent.GameId, 0).Select(a => a.Id));

        planner.ChangeSlotGame(0, games.Single(g => g.Id == recent.GameId));
        Assert.AreEqual(recent.Id, planner.Slots[0].ActivityId);
    }

    // ---- no candidates -------------------------------------------------------------------------

    // Every Activity is recent: the pool would have candidates without the Filter, so each message names it.
    [TestMethod]
    public void WhenAvoidRecentEmptiesThePool_EachMessageNamesIt_AndTheSelectionsAreKept()
    {
        var first = Add();
        var second = Add();
        var third = Add();

        var fresh = Planner(Avoid(first, second, third));
        fresh.RandomizeAll();
        Assert.IsEmpty(fresh.Slots);
        Assert.AreEqual(SlotPlanner.AvoidRecentNoMatchError, fresh.GenerationError);
        StringAssert.Contains(fresh.GenerationError, Advice);

        var planner = Planner(Avoid(first, second, third), first);
        var before = planner.Slots.ToArray();
        planner.RandomizeAll();
        Assert.AreEqual(SlotPlanner.AvoidRecentSelectionsKeptError, planner.GenerationError);
        StringAssert.Contains(planner.GenerationError, Advice);
        StringAssert.Contains(planner.GenerationError, "Existing selections were kept.");
        Assert.AreSequenceEqual(before, planner.Slots.ToArray());

        Assert.IsEmpty(planner.GetRerollCandidates(0));
        Assert.AreEqual(SlotPlanner.RerollAvoidRecentReason, planner.GetRerollBlockedReason(0));
        StringAssert.Contains(planner.GetRerollBlockedReason(0), Advice);
        planner.RerollSlot(0);
        Assert.AreSequenceEqual(before, planner.Slots.ToArray());

        planner.AddSlot();
        Assert.AreEqual(SlotPlanner.AddSlotAvoidRecentReason, planner.AddSlotError);
        StringAssert.Contains(planner.AddSlotError, Advice);
        Assert.AreSequenceEqual(before, planner.Slots.ToArray());
    }

    // The two slots need two Games; only one Game has an Activity that is not recent. Without the Filter the roll
    // works, so the Filter is the cause.
    [TestMethod]
    public void WhenAvoidRecentLeavesTooFewGamesForTheSlots_TheKeptMessageNamesIt()
    {
        var a = Add();
        var b = Add();
        var planner = Planner(Avoid(b), a, b);

        planner.RandomizeAll();

        Assert.AreEqual(SlotPlanner.AvoidRecentSelectionsKeptError, planner.GenerationError);
    }

    // Themed activities only empties the pool on its own: turning Avoid recent off would not help, so the old
    // messages stay, word for word.
    [TestMethod]
    public void WhenAnotherFilterIsTheCause_TheOldMessagesAreUsed()
    {
        var first = Add();
        Add(); // not recent, and has no Theme or Holiday
        var filter = Avoid(first) with { ThemedOnly = true };

        var fresh = Planner(filter);
        fresh.RandomizeAll();
        Assert.AreEqual(NoMatch, fresh.GenerationError);

        var planner = Planner(filter, first);
        planner.RandomizeAll();
        Assert.AreEqual(SelectionsKept, planner.GenerationError);
        Assert.AreEqual(RerollNoMatch, planner.GetRerollBlockedReason(0));
        planner.AddSlot();
        Assert.AreEqual(AddSlotNoMatch, planner.AddSlotError);
        // Turning Avoid recent off does not help: the same slots still have nothing to add.
        planner.Filter = filter with { RecentActivityIds = null };
        Assert.IsEmpty(planner.GetAddSlotCandidates());
        Assert.AreEqual(AddSlotNoMatch, planner.GetAddSlotBlockedReason());
    }

    // Re-roll: the slot holds the only themed Activity, and it is recent. Without avoid recent the only candidate is
    // the slot's own Activity, which a re-roll never picks, so lowering N would not help: the old title stays.
    [TestMethod]
    public void WhenOnlyTheSlotsOwnActivityIsLeftWithoutTheFilter_TheRerollTitleIsTheOldOne()
    {
        var themed = Add();
        themed.ThemeIds = [Guid.NewGuid()];
        Add(); // not recent, but has no Theme or Holiday
        var planner = Planner(Avoid(themed) with { ThemedOnly = true }, themed);

        Assert.AreEqual(RerollNoMatch, planner.GetRerollBlockedReason(0));
    }

    // Randomize All: two slots with Unique games only on, and three Activities of one Game, one of them recent.
    // Without avoid recent two distinct Activities are left, but only one Game, so the roll fails anyway: old message.
    [TestMethod]
    public void WhenUniqueGamesOnlyIsTheCause_TheKeptMessageIsTheOldOne()
    {
        var game = Game();
        var first = Add(game: game);
        var second = Add(game: game);
        var recent = Add(game: game);
        var planner = Planner(Avoid(recent), first, second);

        planner.RandomizeAll();

        Assert.AreEqual(SelectionsKept, planner.GenerationError);
    }

    // Add Activity Slot: the only themed Activity is already in a slot, and the recent one has no Theme. Without avoid
    // recent nothing could be added either, so the old reason stays.
    [TestMethod]
    public void WhenTheOnlyMatchWithoutTheFilterIsAlreadyInASlot_TheAddSlotReasonIsTheOldOne()
    {
        var themed = Add();
        themed.ThemeIds = [Guid.NewGuid()];
        var recent = Add();
        var planner = Planner(Avoid(recent) with { ThemedOnly = true }, themed);

        Assert.AreEqual(AddSlotNoMatch, planner.GetAddSlotBlockedReason());
        planner.AddSlot();
        Assert.AreEqual(AddSlotNoMatch, planner.AddSlotError);
    }

    // With the Filter off a pool with nothing left uses the old messages too.
    [TestMethod]
    public void WithTheFilterOff_TheOldMessagesAreUsed()
    {
        var only = Add();
        var planner = Planner(SlotFilter.Default, only);

        Assert.AreEqual(RerollNoMatch, planner.GetRerollBlockedReason(0));
        planner.AddSlot();
        Assert.AreEqual(AddSlotNoMatch, planner.AddSlotError);
    }

    // On with nothing recent (an empty set) is the same pool as off.
    [TestMethod]
    public void OnWithNothingRecent_OffersEverything()
    {
        var a = Add();
        var b = Add();
        var planner = Planner(SlotFilter.Default with { RecentActivityIds = new HashSet<Guid>() });

        CollectionAssert.AreEquivalent(new[] { a.Id, b.Id }, planner.GetAddSlotCandidates().Select(x => x.Id).ToList());
    }

    // ---- together ------------------------------------------------------------------------------

    [TestMethod]
    public void AvoidRecentAndThemedOnly_Combine()
    {
        var theme = Guid.NewGuid();
        var themedRecent = Add();
        themedRecent.ThemeIds = [theme];
        var themedKept = Add();
        themedKept.HolidayIds = [Guid.NewGuid()];
        Add(); // not recent, but has no Theme or Holiday
        var planner = Planner(Avoid(themedRecent) with { ThemedOnly = true });

        Assert.AreSequenceEqual(new[] { themedKept.Id }, planner.GetAddSlotCandidates().Select(a => a.Id));
    }

    [TestMethod]
    public void AvoidRecentAndPlayersTonight_Combine()
    {
        var fitsRecent = Add(min: 2, max: 6);
        var fits = Add(min: 2, max: 6);
        Add(min: 5); // not recent, but too few players tonight
        var planner = Planner(Avoid(fitsRecent) with { PlayersTonight = 4 });

        Assert.AreSequenceEqual(new[] { fits.Id }, planner.GetAddSlotCandidates().Select(a => a.Id));
    }

    // Players tonight empties the pool on its own: the old message stays even though Avoid recent is on too.
    [TestMethod]
    public void WhenPlayersTonightIsTheCause_TheOldMessageIsUsed()
    {
        var recent = Add(min: 5);
        Add(min: 5);
        var planner = Planner(Avoid(recent) with { PlayersTonight = 4 });

        planner.RandomizeAll();

        Assert.AreEqual(NoMatch, planner.GenerationError);
    }

    // ---- defaults ------------------------------------------------------------------------------

    [TestMethod]
    public void TheDefaultFilter_HasAvoidRecentOff()
    {
        Assert.IsNull(SlotFilter.Default.RecentActivityIds);
    }

    // The same seed rolls the same slots with the default Filter and with the Filter on but nothing recent.
    [TestMethod]
    public void ARollWithNothingRecent_IsTheSameAsWithTheFilterOff()
    {
        for (var g = 0; g < 4; g++)
        {
            var game = Game();
            Add(game: game);
            Add(game: game);
        }
        for (var seed = 0; seed < 20; seed++)
        {
            var off = new SlotPlanner(new Random(seed));
            off.SetLibrary(games, activities);
            var on = new SlotPlanner(new Random(seed)) { Filter = SlotFilter.Default with { RecentActivityIds = new HashSet<Guid>() } };
            on.SetLibrary(games, activities);

            off.RandomizeAll();
            on.RandomizeAll();

            Assert.HasCount(3, off.Slots);
            Assert.AreSequenceEqual(off.Slots.Select(s => s.ActivityId).ToArray(), on.Slots.Select(s => s.ActivityId).ToArray());
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    // Stable sort keys and first-choice draws: a roll takes the first candidate it may.
    private sealed class FirstChoiceRandom : Random
    {
        public override int Next() => 0;
        public override int Next(int maxValue) => 0;
    }

    private static SlotFilter Avoid(params ActivityEntity[] recent) =>
        SlotFilter.Default with { RecentActivityIds = recent.Select(a => a.Id).ToHashSet() };

    private Guid Game()
    {
        var game = new GameEntity { Id = Guid.NewGuid() };
        games.Add(game);
        return game.Id;
    }

    // Adds an Activity to the library, on a new Game unless one is given.
    private ActivityEntity Add(int? min = null, int? max = null, Guid? game = null)
    {
        var activity = new ActivityEntity { Id = Guid.NewGuid(), GameId = game ?? Game(), MinPlayers = min, MaxPlayers = max };
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
