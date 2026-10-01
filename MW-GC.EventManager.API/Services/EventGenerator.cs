using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Shared.Requests;

namespace MW_GC.EventManager.API.Services;

/// <summary>
/// Draws event selections. G = games, A = activities, F = ids in the request's selected-id
/// lists, N = requested slots.
/// <list type="bullet">
/// <item>Filtering and the candidate index are built once per call in O(G + A + F); no mode
/// rescans, regroups or reshuffles the full activity pool per slot.</item>
/// <item>Repeated-games mode: O(1) expected per slot after that (O(N) total slot work). A
/// malformed id repeated across c records costs O(c) once, so removals total O(A).</item>
/// <item>Unique-games mode is augmenting-path (Kuhn/BFS) matching, which guarantees a draw
/// whenever any N-game matching exists: O(1) per game when its first candidate is free,
/// worst case O(V * E) over the candidate edges (backtracking is inherent to the
/// guarantee). Each game's candidates are shuffled lazily on first touch, and the search
/// exits as soon as N matches exist.</item>
/// </list>
/// </summary>
internal sealed class EventGenerator
{
    private readonly Random Rng;

    public EventGenerator() : this(Random.Shared) { }

    internal EventGenerator(Random random) => Rng = random;

    /// <summary>
    /// Port of the reference app's <c>generateEventSelections</c>.
    /// Returns null when constraints cannot be satisfied.
    /// </summary>
    public List<Selection>? Generate(
        IReadOnlyList<Game> games,
        IReadOnlyList<Activity> activities,
        GenerateEventRequest request)
    {
        if (request.SelectedGameIds is null || request.SelectedThemeIds is null || request.SelectedHolidayIds is null)
            return null;

        var index = new CandidateIndex(games, FilterActivities(activities, request));

        return request.UniqueGamesOnly
            ? GenerateUnique(index, request.Count)
            : GenerateAllowRepeatedGames(index, request.Count);
    }

    public static string GenerateName()
    {
        var now = DateTimeOffset.UtcNow;
        return $"Event - {now:MMM d, h:mm tt}";
    }

    // Selected ids become hash sets once and each activity is tested with a plain loop, so
    // filtering allocates nothing per activity. The returned sequence is enumerated exactly
    // once, by CandidateIndex.
    private static IEnumerable<Activity> FilterActivities(IReadOnlyList<Activity> activities, GenerateEventRequest req)
    {
        if (!req.ThemedOnly && req.SelectedGameIds.Count == 0 && req.SelectedThemeIds.Count == 0 && req.SelectedHolidayIds.Count == 0)
            return activities;

        var gameIds = new HashSet<Guid>(req.SelectedGameIds);
        var themeIds = new HashSet<Guid>(req.SelectedThemeIds);
        var holidayIds = new HashSet<Guid>(req.SelectedHolidayIds);
        var themedOnly = req.ThemedOnly;
        return activities.Where(a =>
        {
            var isSelectedGame = gameIds.Count == 0 || gameIds.Contains(a.GameId);
            var hasThemes = themeIds.Count == 0 || ContainsAny(themeIds, a.ThemeIds);
            var hasHolidays = holidayIds.Count == 0 || ContainsAny(holidayIds, a.HolidayIds);
            var isThemed = !themedOnly || a.ThemeIds.Count > 0 || a.HolidayIds.Count > 0;
            return isSelectedGame && hasThemes && hasHolidays && isThemed;
        });
    }

    private static bool ContainsAny(HashSet<Guid> selected, List<Guid> ids)
    {
        foreach (var id in ids)
            if (selected.Contains(id)) return true;
        return false;
    }

    private List<Selection>? GenerateUnique(CandidateIndex index, int count)
    {
        if (count == 0) return [];
        if (index.Pools.Count < count) return null;

        // Match game IDs to activity IDs. Randomize preference, not feasibility:
        // an augmenting path can repair earlier choices, and a failed game does
        // not prevent considering the remaining games. Each successful search
        // adds one match; exhausting all games proves no requested matching exists.
        // Pools are still in game input order here (nothing has been removed), so the
        // shuffle is the only reordering.
        var orderedPools = index.Pools.OrderBy(_ => Rng.Next()).ToArray();
        var byActivity = new Dictionary<Guid, Guid>();       // activity id -> owning game id
        var byGame = new Dictionary<Guid, Activity>();       // game id -> chosen activity

        foreach (var pool in orderedPools)
        {
            TryAugment(pool);
            if (byGame.Count == count)
                return byGame.Select(kvp => new Selection { Game = index.PoolOf(kvp.Key).Game, Activity = kvp.Value }).ToList();
        }
        return null;

        void TryAugment(GamePool start)
        {
            // Iterative search avoids call-stack growth on long reassignment chains.
            var paths = new Dictionary<Guid, (GamePool Pool, Activity Activity)>(); // activity id -> how it was reached
            var pending = new Queue<GamePool>();
            pending.Enqueue(start);
            while (pending.TryDequeue(out var current))
            {
                foreach (var candidate in ChoicesOf(current))
                {
                    var id = candidate.Id;
                    if (!paths.TryAdd(id, (current, candidate))) continue;
                    if (byActivity.TryGetValue(id, out var ownerGameId))
                    {
                        pending.Enqueue(index.PoolOf(ownerGameId));
                        continue;
                    }

                    // Flip the path from this unused ID back to the unmatched game.
                    var nextId = id;
                    while (true)
                    {
                        var (pool, activity) = paths[nextId];
                        var gameId = pool.Game.Id;
                        byGame.TryGetValue(gameId, out var previous);
                        byGame[gameId] = activity;
                        byActivity[activity.Id] = gameId;
                        if (previous is null) return;
                        nextId = previous.Id;
                    }
                }
            }
        }
    }

    // A game's distinct activities in random preference order, computed the first time
    // the search touches the game so a small request never shuffles untouched pools.
    private Activity[] ChoicesOf(GamePool pool) =>
        pool.Choices ??= pool.Candidates
            .Select(c => c.Activity)
            .DistinctBy(a => a.Id)
            .OrderBy(_ => Rng.Next())
            .ToArray();

    private List<Selection>? GenerateAllowRepeatedGames(CandidateIndex index, int count)
    {
        if (index.DistinctActivityCount < count) return null;

        var selections = new List<Selection>(count);
        while (selections.Count < count)
        {
            // Choose only games with unused activities, then one of that game's remaining
            // records. Each slot removes one distinct ID, so while fewer than `count` slots
            // are filled a live pool always exists; exhausted games never consume attempts.
            var pool = index.Pools[Rng.Next(index.Pools.Count)];
            var activity = pool.Candidates[Rng.Next(pool.Candidates.Count)].Activity;
            selections.Add(new Selection { Game = pool.Game, Activity = activity });
            index.Remove(activity.Id);
        }

        return selections;
    }

    // Built once per Generate call from a single pass over the games and a single pass over
    // the filtered activities. Each record has a position in its game pool and an ID reverse
    // index, so swap-removal costs O(1) per record. All copies of an ID are consumed
    // together, including malformed duplicates spanning games. The first game wins for a
    // repeated Game.Id; activities of unknown games add no capacity; games without activities
    // get no live pool.
    private sealed class CandidateIndex
    {
        private readonly Dictionary<Guid, GamePool> poolsByGame;
        private readonly Dictionary<Guid, List<Candidate>> copiesById = new();

        /// <summary>Pools that still hold activities, in game input order until the first removal.</summary>
        public List<GamePool> Pools { get; } = [];

        public int DistinctActivityCount => copiesById.Count;

        public GamePool PoolOf(Guid gameId) => poolsByGame[gameId];

        public CandidateIndex(IReadOnlyList<Game> games, IEnumerable<Activity> activities)
        {
            poolsByGame = new Dictionary<Guid, GamePool>(games.Count);
            var inputOrder = new List<GamePool>(games.Count);
            foreach (var game in games)
            {
                var pool = new GamePool(game);
                if (poolsByGame.TryAdd(game.Id, pool))
                    inputOrder.Add(pool);
            }

            foreach (var activity in activities)
            {
                if (!poolsByGame.TryGetValue(activity.GameId, out var pool)) continue;

                var candidate = new Candidate(activity, pool, pool.Candidates.Count);
                pool.Candidates.Add(candidate);
                if (!copiesById.TryGetValue(activity.Id, out var copies))
                    copiesById.Add(activity.Id, copies = []);
                copies.Add(candidate);
            }

            foreach (var pool in inputOrder)
            {
                if (pool.Candidates.Count == 0) continue;
                pool.Position = Pools.Count;
                Pools.Add(pool);
            }
        }

        public void Remove(Guid id)
        {
            foreach (var candidate in copiesById[id])
            {
                var pool = candidate.Pool;
                var last = pool.Candidates[^1];
                pool.Candidates[candidate.Position] = last;
                last.Position = candidate.Position;
                pool.Candidates.RemoveAt(pool.Candidates.Count - 1);
                if (pool.Candidates.Count != 0) continue;

                var lastPool = Pools[^1];
                Pools[pool.Position] = lastPool;
                lastPool.Position = pool.Position;
                Pools.RemoveAt(Pools.Count - 1);
            }
            copiesById.Remove(id);
        }
    }

    private sealed class GamePool(Game game)
    {
        public Game Game { get; } = game;
        public List<Candidate> Candidates { get; } = [];
        public int Position { get; set; }
        public Activity[]? Choices { get; set; }
    }

    private sealed class Candidate(Activity activity, GamePool pool, int position)
    {
        public Activity Activity { get; } = activity;
        public GamePool Pool { get; } = pool;
        public int Position { get; set; } = position;
    }
}
