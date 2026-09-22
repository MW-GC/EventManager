using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Shared.Requests;

namespace MW_GC.EventManager.API.Services;

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
        var filtered = FilterActivities(activities, request);

        var gamesWithActivities = games
            .Where(g => filtered.Any(a => a.GameId == g.Id))
            .ToList();

        return request.UniqueGamesOnly
            ? GenerateUnique(gamesWithActivities, filtered, request.Count)
            : GenerateAllowRepeatedGames(gamesWithActivities, filtered, request.Count);
    }

    public static string GenerateName()
    {
        var now = DateTimeOffset.UtcNow;
        return $"Event - {now:MMM d, h:mm tt}";
    }

    private static List<Activity> FilterActivities(IReadOnlyList<Activity> activities, GenerateEventRequest req)
    {
        if (!req.ThemedOnly && req.SelectedGameIds.Count == 0 && req.SelectedThemeIds.Count == 0 && req.SelectedHolidayIds.Count == 0)
            return activities.ToList();

        return activities.Where(a =>
        {
            var isSelectedGame = req.SelectedGameIds.Count == 0 || req.SelectedGameIds.Contains(a.GameId);
            var hasThemes = req.SelectedThemeIds.Count == 0 || a.ThemeIds.Intersect(req.SelectedThemeIds).Any();
            var hasHolidays = req.SelectedHolidayIds.Count == 0 || a.HolidayIds.Intersect(req.SelectedHolidayIds).Any();
            var isThemed = !req.ThemedOnly || a.ThemeIds.Count > 0 || a.HolidayIds.Count > 0;
            return isSelectedGame && hasThemes && hasHolidays && isThemed;
        }).ToList();
    }

    private List<Selection>? GenerateUnique(List<Game> games, List<Activity> activities, int count)
    {
        if (count == 0) return [];
        if (games.Count < count) return null;

        // Match game IDs to activity IDs. Randomize preference, not feasibility:
        // an augmenting path can repair earlier choices, and a failed game does
        // not prevent considering the remaining games. Each successful search
        // adds one match; exhausting all games proves no requested matching exists.
        var orderedGames = games.OrderBy(_ => Rng.Next()).ToArray();
        var choices = orderedGames.ToDictionary(
            g => g.Id,
            g => activities
                .Where(a => a.GameId == g.Id)
                .DistinctBy(a => a.Id)
                .OrderBy(_ => Rng.Next())
                .ToArray());
        var byActivity = new Dictionary<Guid, Activity>();   // activity id -> chosen activity
        var byGame = new Dictionary<Guid, Activity>();        // game id -> chosen activity
        var gameById = orderedGames.ToDictionary(g => g.Id);

        foreach (var game in orderedGames)
        {
            TryAugment(game);
            if (byGame.Count == count)
                return byGame.Select(kvp => new Selection { Game = gameById[kvp.Key], Activity = kvp.Value }).ToList();
        }
        return null;

        void TryAugment(Game start)
        {
            // Iterative search avoids call-stack growth on long reassignment chains.
            var paths = new Dictionary<Guid, (Guid GameId, Activity Activity)>(); // activity id -> how it was reached
            var pending = new Queue<Guid>();
            pending.Enqueue(start.Id);
            while (pending.TryDequeue(out var currentGameId))
            {
                foreach (var candidate in choices[currentGameId])
                {
                    var id = candidate.Id;
                    if (!paths.TryAdd(id, (currentGameId, candidate))) continue;
                    if (byActivity.TryGetValue(id, out var owner))
                    {
                        pending.Enqueue(owner.GameId);
                        continue;
                    }

                    // Flip the path from this unused ID back to the unmatched game.
                    var nextId = id;
                    while (true)
                    {
                        var (gameId, activity) = paths[nextId];
                        byGame.TryGetValue(gameId, out var previous);
                        byGame[gameId] = activity;
                        byActivity[activity.Id] = new Activity { Id = activity.Id, Name = activity.Name, GameId = gameId };
                        if (previous is null) return;
                        nextId = previous.Id;
                    }
                }
            }
        }
    }

    private List<Selection>? GenerateAllowRepeatedGames(List<Game> games, List<Activity> activities, int count)
    {
        var knownGames = games.Select(g => g.Id).ToHashSet();
        var remaining = activities.Where(a => knownGames.Contains(a.GameId)).ToList();
        if (remaining.Select(a => a.Id).Distinct().Count() < count) return null;

        var selections = new List<Selection>(count);
        while (selections.Count < count)
        {
            // Choose only games with unused activities. Exhausted games must not
            // consume attempts or cause a valid request to fail randomly.
            var groups = remaining.GroupBy(a => a.GameId).ToList();
            var candidates = groups[Rng.Next(groups.Count)].ToList();
            var activity = candidates[Rng.Next(candidates.Count)];
            var game = games.First(g => g.Id == activity.GameId);
            selections.Add(new Selection { Game = game, Activity = activity });
            remaining.RemoveAll(a => a.Id == activity.Id);
        }

        return selections;
    }
}
