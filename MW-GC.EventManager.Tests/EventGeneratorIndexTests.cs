using System.Collections;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Shared.Requests;
using Xunit;

namespace MW_GC.EventManager.Tests;

public class EventGeneratorIndexTests
{
    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);
    private static Game Game(int value) => new() { Id = Id(value), Name = $"Game {value}" };
    private static Activity Activity(int value, Game game) => new() { Id = Id(value), Name = $"Activity {value}", GameId = game.Id };

    private static void AssertSameIds(IEnumerable<Guid> expected, IEnumerable<Guid> actual) =>
        Assert.Equal(expected.OrderBy(id => id), actual.OrderBy(id => id));

    // Counts every element handed out, through either the indexer or an enumerator, so a
    // rescan of the list shows up as a read count far above the list size. Deliberately not
    // an ICollection<T>, so LINQ cannot bulk-copy it without enumerating.
    private sealed class CountingList<T>(IReadOnlyList<T> inner) : IReadOnlyList<T>
    {
        public long Reads { get; private set; }
        public int Count => inner.Count;

        public T this[int index]
        {
            get
            {
                Reads++;
                return inner[index];
            }
        }

        public IEnumerator<T> GetEnumerator()
        {
            foreach (var item in inner)
            {
                Reads++;
                yield return item;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CountingRandom(int seed) : Random(seed)
    {
        public int Calls { get; private set; }

        public override int Next()
        {
            Calls++;
            return base.Next();
        }

        public override int Next(int maxValue)
        {
            Calls++;
            return base.Next(maxValue);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratingEverySlotReadsEachInputListAFixedNumberOfTimes(bool unique)
    {
        var games = Enumerable.Range(1, 500).Select(Game).ToArray();
        var activities = games
            .SelectMany((g, i) => Enumerable.Range(0, 4).Select(slot => Activity(i * 4 + slot + 1, g)))
            .ToArray();
        var countedGames = new CountingList<Game>(games);
        var countedActivities = new CountingList<Activity>(activities);
        var count = unique ? games.Length : activities.Length;

        var result = new EventGenerator().Generate(countedGames, countedActivities, new() { Count = count, UniqueGamesOnly = unique });

        Assert.NotNull(result);
        Assert.Equal(count, result.Count);
        Assert.Equal(count, result.Select(s => s.Activity.Id).Distinct().Count());
        Assert.All(result, s => Assert.Equal(s.Game.Id, s.Activity.GameId));
        if (unique)
            AssertSameIds(games.Select(g => g.Id), result.Select(s => s.Game.Id));
        else
            AssertSameIds(activities.Select(a => a.Id), result.Select(s => s.Activity.Id));

        // One pass each is expected; 3x leaves room for an extra pass but fails on any
        // per-slot (or per-game) rescan, which costs hundreds of thousands of reads here.
        Assert.True(countedGames.Reads <= 3L * games.Length, $"games list read {countedGames.Reads} times for {games.Length} games");
        Assert.True(countedActivities.Reads <= 3L * activities.Length, $"activities list read {countedActivities.Reads} times for {activities.Length} activities");
    }

    [Fact]
    public void RepeatedGamesModeAllocatesLinearlyNotPerSlot()
    {
        // The input lists are copied before any per-slot work, so read counts alone cannot
        // see a per-slot GroupBy/ToList/RemoveAll. Those allocate O(pool) on every slot,
        // which makes allocated bytes a deterministic (not wall-clock) cost proxy.
        var games = Enumerable.Range(1, 500).Select(Game).ToArray();
        var activities = games
            .SelectMany((g, i) => Enumerable.Range(0, 4).Select(slot => Activity(i * 4 + slot + 1, g)))
            .ToArray();
        var generator = new EventGenerator(new Random(7));
        var request = new GenerateEventRequest { Count = activities.Length, UniqueGamesOnly = false };
        Assert.NotNull(generator.Generate(games, activities, request)); // Warm up JIT and statics.

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = generator.Generate(games, activities, request);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotNull(result);
        Assert.Equal(activities.Length, result.Select(s => s.Activity.Id).Distinct().Count());
        // Linear allocation is a few hundred bytes per activity; regrouping the pool on
        // every slot costs megabytes per hundred slots.
        Assert.True(allocated <= 2000L * activities.Length, $"{allocated} bytes allocated for {activities.Length} activities");
    }

    [Fact]
    public void LargeSkewedPoolExhaustsEveryDistinctIdWithoutMutatingInputs()
    {
        var games = Enumerable.Range(1, 2000).Select(Game).ToArray();
        var activities = games.Select((g, i) => Activity(i + 1, g)).ToList();
        activities.AddRange(Enumerable.Range(2001, 20000).Select(i => Activity(i, games[0])));
        activities.AddRange(activities.Take(100).ToArray()); // Duplicate records add no capacity.
        var originalGames = games.ToArray();
        var originalActivities = activities.ToArray();
        var expected = activities.Select(a => a.Id).Distinct().ToArray();
        var request = new GenerateEventRequest { Count = expected.Length, UniqueGamesOnly = false };
        var generator = new EventGenerator();

        // Assert sets, not random ordering or elapsed time. Reuse the same inputs and
        // generator to catch state leaking between generation requests.
        for (var run = 0; run < 2; run++)
        {
            var result = generator.Generate(games, activities, request);
            Assert.NotNull(result);
            AssertSameIds(expected, result.Select(s => s.Activity.Id));
            Assert.All(result, s => Assert.Equal(s.Game.Id, s.Activity.GameId));
            Assert.Equal(originalGames, games);
            Assert.Equal(originalActivities, activities);
        }
        Assert.Null(generator.Generate(games, activities, request with { Count = expected.Length + 1 }));
    }

    [Fact]
    public void DuplicateIdsAcrossPoolsAreRemovedTogetherIncludingExhaustedGames()
    {
        var games = Enumerable.Range(1, 300).Select(Game).ToArray();
        // Ids 1 and 2 appear in every game (and id 1 twice per game); id 3 only in game 1.
        var activities = games.SelectMany(g => new[] { Activity(1, g), Activity(1, g), Activity(2, g) }).ToList();
        activities.Add(Activity(3, games[0]));
        var generator = new EventGenerator();

        for (var run = 0; run < 20; run++)
        {
            var result = generator.Generate(games, activities, new() { Count = 3, UniqueGamesOnly = false });
            Assert.NotNull(result);
            AssertSameIds([Id(1), Id(2), Id(3)], result.Select(s => s.Activity.Id));
            Assert.All(result, s => Assert.Equal(s.Game.Id, s.Activity.GameId));
        }
        Assert.Null(generator.Generate(games, activities, new() { Count = 4, UniqueGamesOnly = false }));
    }

    [Fact]
    public void UniqueModeFailsWhenSharedIdsMakeARequiredGameUnsatisfiable()
    {
        var first = Game(1);
        var second = Game(2);
        var third = Game(3);
        Activity[] activities = [Activity(1, first), Activity(1, second), Activity(2, third), Activity(3, third)];
        var generator = new EventGenerator();

        // First and second can only share id 1, so at most two games can be matched.
        Assert.Null(generator.Generate([first, second, third], activities, new() { Count = 3, UniqueGamesOnly = true }));

        var feasible = generator.Generate([first, second, third], activities, new() { Count = 2, UniqueGamesOnly = true });
        Assert.NotNull(feasible);
        Assert.Equal(2, feasible.Select(s => s.Game.Id).Distinct().Count());
        Assert.Equal(2, feasible.Select(s => s.Activity.Id).Distinct().Count());
        Assert.All(feasible, s => Assert.Equal(s.Game.Id, s.Activity.GameId));
    }

    [Fact]
    public void LargeUniqueRequestUsesEveryGameOnceAndReturnsInputActivities()
    {
        var games = Enumerable.Range(1, 2000).Select(Game).ToArray();
        var activities = games.SelectMany((g, i) => new[] { Activity(i * 2 + 1, g), Activity(i * 2 + 2, g) }).ToArray();
        var inputActivities = new HashSet<Activity>(activities, ReferenceEqualityComparer.Instance);
        var generator = new EventGenerator();

        var result = generator.Generate(games, activities, new() { Count = games.Length, UniqueGamesOnly = true });

        Assert.NotNull(result);
        AssertSameIds(games.Select(g => g.Id), result.Select(s => s.Game.Id));
        Assert.Equal(games.Length, result.Select(s => s.Activity.Id).Distinct().Count());
        Assert.All(result, s => Assert.Equal(s.Game.Id, s.Activity.GameId));
        Assert.All(result, s => Assert.Contains(s.Activity, inputActivities)); // The real record, not a copy.
        Assert.Null(generator.Generate(games, activities, new() { Count = games.Length + 1, UniqueGamesOnly = true }));
    }

    [Fact]
    public void TinyUniqueRequestOverHugePoolsOnlyShufflesTheGamesItTouches()
    {
        var games = Enumerable.Range(1, 1000).Select(Game).ToArray();
        var activities = games.SelectMany((g, i) => Enumerable.Range(0, 20).Select(slot => Activity(i * 20 + slot + 1, g))).ToArray();
        var random = new CountingRandom(12345);

        var result = new EventGenerator(random).Generate(games, activities, new() { Count = 3, UniqueGamesOnly = true });

        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.Equal(3, result.Select(s => s.Game.Id).Distinct().Count());
        Assert.Equal(3, result.Select(s => s.Activity.Id).Distinct().Count());
        Assert.All(result, s => Assert.Equal(s.Game.Id, s.Activity.GameId));
        Assert.All(result, s => Assert.Contains(s.Activity, activities));

        // Every first choice is free here, so the search touches exactly 3 games: one
        // random key per game for the game shuffle plus one per record of each touched
        // pool. Shuffling every pool eagerly would cost 1000 + 1000 * 20 draws.
        Assert.True(random.Calls <= games.Length + 3 * 20, $"{random.Calls} random draws for a 3-game request");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IndexedPoolsRespectCombinedFiltersAndIgnoreOrphanCapacity(bool unique)
    {
        var game = Game(1);
        var other = Game(2);
        var orphan = Game(3); // Never passed in the games list.
        var eligible = Activity(1, game);
        eligible.ThemeIds.Add(Id(10));
        eligible.HolidayIds.Add(Id(20));
        var wrongGame = Activity(2, other);
        wrongGame.ThemeIds.Add(Id(10));
        wrongGame.HolidayIds.Add(Id(20));
        var orphanActivity = Activity(3, orphan); // Satisfies every filter but has no known game.
        orphanActivity.ThemeIds.Add(Id(10));
        orphanActivity.HolidayIds.Add(Id(20));
        var themeOnly = Activity(4, game);
        themeOnly.ThemeIds.Add(Id(10));
        var holidayOnly = Activity(5, game);
        holidayOnly.HolidayIds.Add(Id(20));
        var unthemed = Activity(6, game);
        Activity[] activities = [eligible, wrongGame, orphanActivity, themeOnly, holidayOnly, unthemed];
        var request = new GenerateEventRequest
        {
            Count = 1,
            UniqueGamesOnly = unique,
            ThemedOnly = true,
            SelectedGameIds = [game.Id, orphan.Id],
            SelectedThemeIds = [Id(10)],
            SelectedHolidayIds = [Id(20)],
        };
        var generator = new EventGenerator();

        var result = generator.Generate([game, other], activities, request);
        Assert.NotNull(result);
        var selection = Assert.Single(result);
        Assert.Equal(eligible.Id, selection.Activity.Id);
        Assert.Equal(game.Id, selection.Game.Id);

        // The orphan's activity must not add capacity, and no games means no capacity at all.
        Assert.Null(generator.Generate([game, other], activities, request with { Count = 2 }));
        Assert.Null(generator.Generate([], activities, request));

        // Dropping the theme filter admits the holiday-only activity (themed via its holiday).
        var holidayResult = generator.Generate([game], [holidayOnly], request with { SelectedThemeIds = [] });
        Assert.NotNull(holidayResult);
        Assert.Equal(holidayOnly.Id, Assert.Single(holidayResult).Activity.Id);

        // An unthemed activity stays excluded while ThemedOnly is set ...
        Assert.Null(generator.Generate([game], [unthemed], request with { SelectedThemeIds = [], SelectedHolidayIds = [] }));
        // ... and is admitted once every filter is cleared.
        var cleared = generator.Generate([game], [unthemed],
            new GenerateEventRequest { Count = 1, UniqueGamesOnly = unique });
        Assert.NotNull(cleared);
        Assert.Equal(unthemed.Id, Assert.Single(cleared).Activity.Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ZeroCountReturnsAnEmptyNonNullList(bool unique)
    {
        var game = Game(1);

        var empty = new EventGenerator().Generate([], [], new() { Count = 0, UniqueGamesOnly = unique });
        Assert.NotNull(empty);
        Assert.Empty(empty);

        var withData = new EventGenerator().Generate([game], [Activity(1, game)], new() { Count = 0, UniqueGamesOnly = unique });
        Assert.NotNull(withData);
        Assert.Empty(withData);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RepeatedGameIdsAreToleratedFirstGameWins(bool unique)
    {
        var first = Game(1);
        var impostor = first with { Name = "Same id, listed later" };
        var activity = Activity(1, first);

        var result = new EventGenerator().Generate([first, impostor], [activity], new() { Count = 1, UniqueGamesOnly = unique });

        Assert.NotNull(result);
        var selection = Assert.Single(result);
        Assert.Same(first, selection.Game);
        Assert.Same(activity, selection.Activity);
    }
}
