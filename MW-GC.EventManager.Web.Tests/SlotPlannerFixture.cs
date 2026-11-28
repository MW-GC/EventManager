using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Services;
using static MW_GC.EventManager.Web.Tests.SlotTestData;

namespace MW_GC.EventManager.Web.Tests;

// The customize dialog's slot-planning rules (issue #58), driven through SlotPlanner's public API. These tests
// moved from SlotRerollTests, which reached the same rules through the Events page's private members; their names
// and assertions are unchanged. The library is
// two Games: gameA holds current and replacement (replacement tagged with theme and holiday), gameB holds other; the
// two slots start as current and other. The tests on it were split by behaviour from SlotPlannerTests (#268):
// SlotPlannerTests, SlotPlannerRandomizeTests, SlotPlannerUniqueMatchingTests, SlotPlannerPoolTests,
// SlotPlannerGenerationErrorTests and SlotPlannerAddSlotTests.
public abstract class SlotPlannerFixture
{
    protected readonly Guid gameA = Guid.NewGuid();
    protected readonly Guid gameB = Guid.NewGuid();
    protected readonly Guid theme = Guid.NewGuid();
    protected readonly Guid holiday = Guid.NewGuid();
    protected readonly ActivityEntity current;
    protected readonly ActivityEntity other;
    protected readonly ActivityEntity replacement;
    protected List<GameEntity> games;
    protected List<ActivityEntity> activities;
    protected SlotPlanner planner;

    protected SlotPlannerFixture()
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

    protected IReadOnlyList<SlotSelection> Slots => planner.Slots;

    // The library the planner reads. Tests change it in place, as the page tests changed the page's lists.
    protected List<ActivityEntity> Activities => activities;

    protected List<ActivityEntity> Candidates() => planner.GetRerollCandidates(0);

    protected SlotPlanner NewPlanner(Random random)
    {
        var p = new SlotPlanner(random);
        p.SetLibrary(games, activities);
        return p;
    }

    // Swaps in a planner that rolls with random, keeping the library, filters, slots and errors.
    protected void UseRandom(Random random)
    {
        var p = NewPlanner(random);
        p.Filter = planner.Filter;
        p.SetSlots(planner.Slots);
        p.GenerationError = planner.GenerationError;
        p.AddSlotError = planner.AddSlotError;
        planner = p;
    }

    protected void SetGames(List<GameEntity> library)
    {
        games = library;
        planner.SetLibrary(games, activities);
    }

    protected void SetActivities(List<ActivityEntity> library)
    {
        activities = library;
        planner.SetLibrary(games, activities);
    }

    protected void Unique(bool on) => planner.Filter = planner.Filter with { UniqueGames = on };

    // Sets one id filter by the page field it is bound to there; the names keep the moved tests' data rows.
    protected void SetIds(string field, params Guid[] ids) => planner.Filter = field switch
    {
        "_custGameIds" => planner.Filter with { GameIds = ids },
        "_custThemeIds" => planner.Filter with { ThemeIds = ids },
        "_custHolidayIds" => planner.Filter with { HolidayIds = ids },
        _ => throw new ArgumentOutOfRangeException(nameof(field), field)
    };

    protected void AddSlot(ActivityEntity activity) => planner.SetSlots([.. planner.Slots, Slot(activity)]);

    protected void SetSlots(params ActivityEntity[] slots) => planner.SetSlots(slots.Select(Slot));

    // Replaces the fixture library with one game per entry, holding that many activities each.
    protected List<GameEntity> UseLibrary(params int[] activitiesPerGame)
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

    protected void AssertFilledSlots(int expected, bool distinctGames)
    {
        Assert.AreEqual(expected, Slots.Count);
        Assert.IsNull(planner.GenerationError);
        Assert.AreEqual(expected, Slots.Select(s => s.ActivityId).Distinct().Count());
        if (distinctGames) Assert.AreEqual(expected, Slots.Select(s => s.GameId).Distinct().Count());
    }
}
