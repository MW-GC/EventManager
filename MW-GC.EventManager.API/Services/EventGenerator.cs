using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Shared.Requests;

namespace MW_GC.EventManager.API.Services;

internal sealed class EventGenerator
{
    private static readonly Random Rng = Random.Shared;

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
            : GenerateAllowDuplicates(gamesWithActivities, filtered, request.Count);
    }

    public static string GenerateName()
    {
        var now = DateTimeOffset.UtcNow;
        return $"Event - {now:MMM d, h:mm tt}";
    }

    private static List<Activity> FilterActivities(IReadOnlyList<Activity> activities, GenerateEventRequest req)
    {
        if (!req.ThemedOnly && req.SelectedThemeIds.Count == 0 && req.SelectedHolidayIds.Count == 0)
            return activities.ToList();

        return activities.Where(a =>
        {
            var hasThemes = req.SelectedThemeIds.Count == 0 || a.ThemeIds.Intersect(req.SelectedThemeIds).Any();
            var hasHolidays = req.SelectedHolidayIds.Count == 0 || a.HolidayIds.Intersect(req.SelectedHolidayIds).Any();
            var isThemed = !req.ThemedOnly || a.ThemeIds.Count > 0 || a.HolidayIds.Count > 0;
            return hasThemes && hasHolidays && isThemed;
        }).ToList();
    }

    private static List<Selection>? GenerateUnique(List<Game> games, List<Activity> activities, int count)
    {
        if (games.Count < count) return null;

        var shuffled = games.OrderBy(_ => Rng.Next()).Take(count).ToList();
        var usedActivityIds = new HashSet<Guid>();
        var selections = new List<Selection>(count);

        foreach (var game in shuffled)
        {
            var candidates = activities.Where(a => a.GameId == game.Id && !usedActivityIds.Contains(a.Id)).ToList();
            if (candidates.Count == 0) return null;

            var activity = candidates[Rng.Next(candidates.Count)];
            usedActivityIds.Add(activity.Id);
            selections.Add(new Selection { Game = game, Activity = activity });
        }

        return selections;
    }

    private static List<Selection>? GenerateAllowDuplicates(List<Game> games, List<Activity> activities, int count)
    {
        if (games.Count == 0) return null;

        var usedActivityIds = new HashSet<Guid>();
        var selections = new List<Selection>(count);
        var maxAttempts = count * 50;

        for (var attempt = 0; attempt < maxAttempts && selections.Count < count; attempt++)
        {
            var game = games[Rng.Next(games.Count)];
            var candidates = activities.Where(a => a.GameId == game.Id && !usedActivityIds.Contains(a.Id)).ToList();
            if (candidates.Count == 0) continue;

            var activity = candidates[Rng.Next(candidates.Count)];
            usedActivityIds.Add(activity.Id);
            selections.Add(new Selection { Game = game, Activity = activity });
        }

        return selections.Count == count ? selections : null;
    }
}
