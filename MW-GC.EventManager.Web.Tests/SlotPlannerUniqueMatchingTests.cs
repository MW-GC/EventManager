using MW_GC.EventManager.Shared.Entities;
using static MW_GC.EventManager.Web.Tests.SlotTestData;

namespace MW_GC.EventManager.Web.Tests;

// Randomize All with Unique games only on: one Activity per Game and no Activity twice is a matching problem,
// so a greedy dead end must be repaired rather than reported. Fixture: SlotPlannerFixture.
[TestClass]
public class SlotPlannerUniqueMatchingTests : SlotPlannerFixture
{
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
}
