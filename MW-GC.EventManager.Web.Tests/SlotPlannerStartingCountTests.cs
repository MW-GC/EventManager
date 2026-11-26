using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Tests;

// #246 item 5: Randomize All on a fresh dialog aims for StartingSlotCount, kept to 1 to 5 slots whatever it is set to.
// Six Games of one Activity each, so the library never limits the roll below 5.
[TestClass]
public sealed class SlotPlannerStartingCountTests
{
    [TestMethod]
    [DataRow(0, 1, true)]
    [DataRow(0, 1, false)]
    [DataRow(9, 5, true)]
    [DataRow(9, 5, false)]
    public void AStartingSlotCountOutsideOneToFive_RollsTheNearestOfThem(int starting, int expected, bool uniqueGames)
    {
        var games = Enumerable.Range(0, 6).Select(i => new GameEntity { Name = $"Game {i}" }).ToList();
        var activities = games.Select(g => new ActivityEntity { Name = $"{g.Name} run", GameId = g.Id }).ToList();
        var planner = new SlotPlanner(new Random(7)) { StartingSlotCount = starting };
        planner.SetLibrary(games, activities);
        planner.Filter = SlotFilter.Default with { UniqueGames = uniqueGames };

        planner.RandomizeAll();

        Assert.HasCount(expected, planner.Slots);
        Assert.IsNull(planner.GenerationError);
    }
}
