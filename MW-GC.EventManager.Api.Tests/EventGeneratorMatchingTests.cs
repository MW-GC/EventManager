using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.Shared.Models;

namespace MW_GC.EventManager.Api.Tests;

[TestClass]
public class EventGeneratorMatchingTests
{
    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);
    private static Game Game(int value) => new() { Id = Id(value), Name = $"Game {value}" };
    private static Activity Activity(int value, Game game) => new() { Id = Id(value), Name = $"Activity {value}", GameId = game.Id };

    // Stable random keys and first-choice draws force the old greedy dead ends.
    private sealed class FirstChoiceRandom : Random
    {
        public override int Next() => 0;
        public override int Next(int maxValue) => 0;
    }

    [TestMethod]
    public void UniqueGamesReassignsEarlierActivityInsteadOfFailing()
    {
        var first = Game(1);
        var second = Game(2);
        Activity[] activities = [Activity(1, first), Activity(2, first), Activity(1, second)];
        var original = activities.ToArray();
        var generator = new EventGenerator(new FirstChoiceRandom());

        for (var run = 0; run < 2; run++)
        {
            var result = generator.Generate([first, second], activities, new() { Count = 2, UniqueGamesOnly = true });
            Assert.IsNotNull(result);
            Assert.AreEqual(2, result.Count);
            Assert.AreEqual(Id(2), result.Single(s => s.Game.Id == first.Id).Activity.Id);
            Assert.AreEqual(Id(1), result.Single(s => s.Game.Id == second.Id).Activity.Id);
            foreach (var s in result) Assert.AreEqual(s.Game.Id, s.Activity.GameId);
            Assert.AreSequenceEqual(original, activities);
        }
    }

    [TestMethod]
    public void UniqueGamesConsidersGamesBeyondAnUnmatchableInitialSubset()
    {
        var first = Game(1);
        var second = Game(2);
        var third = Game(3);
        var result = new EventGenerator(new FirstChoiceRandom()).Generate([first, second, third],
            [Activity(1, first), Activity(1, second), Activity(2, third)],
            new() { Count = 2, UniqueGamesOnly = true });
        Assert.IsNotNull(result);
        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(2, result.Select(s => s.Game.Id).Distinct().Count());
        Assert.AreSequenceEqual(new[] { Id(1), Id(2) }.OrderBy(g => g), result.Select(s => s.Activity.Id).OrderBy(g => g));
        foreach (var s in result) Assert.AreEqual(s.Game.Id, s.Activity.GameId);
    }

    [TestMethod]
    public void UniqueGamesMatchesExhaustiveSmallGraphFeasibility()
    {
        var games = Enumerable.Range(1, 3).Select(Game).ToArray();
        // Every bipartite graph on three games and three activity IDs, including
        // disconnected graphs, shared IDs, and Hall-deficient subsets.
        for (var mask = 0; mask < 512; mask++)
        {
            var activities = new List<Activity>();
            for (var game = 0; game < 3; game++)
                for (var activity = 0; activity < 3; activity++)
                    if ((mask & (1 << (game * 3 + activity))) != 0)
                        activities.Add(Activity(activity + 1, games[game]));

            var capacity = MaximumMatches(0, 0);
            var generator = new EventGenerator(new Random(mask));
            for (var count = 0; count <= 3; count++)
            {
                var result = generator.Generate(games, activities, new() { Count = count, UniqueGamesOnly = true });
                Assert.IsTrue(count <= capacity == (result is not null), $"Graph {mask}, count {count}");
                if (result is null) continue;
                Assert.AreEqual(count, result.Count);
                Assert.AreEqual(count, result.Select(s => s.Game.Id).Distinct().Count());
                Assert.AreEqual(count, result.Select(s => s.Activity.Id).Distinct().Count());
                foreach (var selection in result)
                {
                    Assert.AreEqual(selection.Game.Id, selection.Activity.GameId);
                    Assert.Contains(selection.Activity, activities);
                }
            }

            // Independent brute-force oracle: skip a game or assign each unused ID.
            int MaximumMatches(int game, int used)
            {
                if (game == 3) return 0;
                var best = MaximumMatches(game + 1, used);
                for (var activity = 0; activity < 3; activity++)
                    if ((used & (1 << activity)) == 0 && (mask & (1 << (game * 3 + activity))) != 0)
                        best = Math.Max(best, 1 + MaximumMatches(game + 1, used | (1 << activity)));
                return best;
            }
        }
    }

    [TestMethod]
    public void UniqueGamesRepairsAMultiHopAssignmentChain()
    {
        var games = Enumerable.Range(1, 4).Select(Game).ToArray();
        var result = new EventGenerator(new FirstChoiceRandom()).Generate(games,
            [Activity(1, games[0]), Activity(2, games[0]),
             Activity(2, games[1]), Activity(3, games[1]),
             Activity(3, games[2]), Activity(4, games[2]), Activity(1, games[3])],
            new() { Count = 4, UniqueGamesOnly = true });
        Assert.IsNotNull(result);
        Assert.AreEqual(4, result.Count);
        Assert.AreSequenceEqual(games.Select(g => g.Id).OrderBy(g => g), result.Select(s => s.Game.Id).OrderBy(g => g));
        Assert.AreSequenceEqual(new[] { Id(1), Id(2), Id(3), Id(4) }.OrderBy(g => g), result.Select(s => s.Activity.Id).OrderBy(g => g));
        foreach (var s in result) Assert.AreEqual(s.Game.Id, s.Activity.GameId);
    }
}
