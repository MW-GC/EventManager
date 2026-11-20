using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;

namespace MW_GC.EventManager.Web.Services;

/// <summary>One Game's row on the Stats page.</summary>
/// <param name="Offered">Events that offered the Game, each Event counted once.</param>
/// <param name="Wins">Events whose winning Selection has the Game.</param>
/// <param name="LastOffered">The latest Date among the Events that offered it, or null for never.</param>
/// <param name="Deleted">True when the Game is no longer in the library; its name then comes from a Selection snapshot.</param>
public sealed record GamePlayStats(Guid Id, string Name, int Offered, int Wins, DateTimeOffset? LastOffered, bool Deleted);

/// <summary>
/// One Activity's row on the Stats page, with the name of its Game, and its Activity performance (#136)
/// over the same Events that <see cref="Offered"/> counts.
/// </summary>
public sealed record ActivityPlayStats(Guid Id, string Name, string GameName, int Offered, int Wins, DateTimeOffset? LastOffered, bool Deleted)
{
    /// <summary>Wins divided by Offered (0 to 1); null when the Activity was never offered, never 0.</summary>
    public decimal? WinRate { get; init; }

    /// <summary>The mean Like count of the Events it was offered at, an Event with no metrics item counting 0; null when never offered.</summary>
    public decimal? AverageLikes { get; init; }

    /// <summary>
    /// The mean Attendees of the Events it was offered at that have Attendees recorded. An Event with no
    /// metrics, or with Attendees cleared, is left out, not counted as 0; null when none has it.
    /// </summary>
    public decimal? AverageAttendees { get; init; }
}

public sealed record PlayStatsReport(IReadOnlyList<GamePlayStats> Games, IReadOnlyList<ActivityPlayStats> Activities);

/// <summary>
/// Play stats for every Game and Activity, computed from the Events list (issue #96). Only Events dated
/// on or before now count. Items are matched by Id, never by name. A Game or Activity that is no longer
/// in the library still gets a row, named from the newest Selection snapshot that mentions it and marked
/// deleted; a library item that was never offered gets a row with 0, 0 and never. Rows come back in the
/// page's default order: never offered first, then the longest unused, then the most offered, then by name.
/// Each Activity also gets its Activity performance (#136, ADR 0001) from the Event metrics and Like
/// counts, matched to the Events by Event id; an item for an Event not in the list is ignored.
/// </summary>
public sealed class PlayStats(TimeProvider time)
{
    /// <summary>Shown for an Activity whose Game is neither in the library nor in any snapshot.</summary>
    public const string UnknownGame = "—";

    public PlayStatsReport Compute(IEnumerable<EventEntity> events, IEnumerable<GameEntity> games, IEnumerable<ActivityEntity> activities,
        IEnumerable<EventMetricsWithLikes> metrics)
    {
        var now = time.GetUtcNow();
        var gameTallies = new Dictionary<Guid, Tally>();
        var activityTallies = new Dictionary<Guid, Tally>();
        var gameSnapshots = new Dictionary<Guid, (DateTimeOffset Date, Game Game)>();
        var activitySnapshots = new Dictionary<Guid, (DateTimeOffset Date, Activity Activity)>();
        var metricsByEvent = new Dictionary<Guid, EventMetricsWithLikes>();
        foreach (var m in metrics ?? []) if (m is not null) metricsByEvent.TryAdd(m.EventId, m);

        foreach (var ev in events)
        {
            if (ev is null || ev.Date > now) continue;
            var selections = (ev.Selections ?? []).Where(s => s?.Game is not null && s.Activity is not null).ToList();
            foreach (var s in selections)
            {
                Remember(gameSnapshots, s.Game.Id, ev.Date, s.Game);
                Remember(activitySnapshots, s.Activity.Id, ev.Date, s.Activity);
            }
            // A Game in two Selections of one Event was offered once at that Event.
            foreach (var gameId in selections.Select(s => s.Game.Id).Distinct())
                TallyFor(gameTallies, gameId).Offer(ev.Date);
            // No metrics item: 0 Likes and no attendees, so the Event stays out of the attendee average.
            var eventMetrics = metricsByEvent.GetValueOrDefault(ev.Id);
            foreach (var activityId in selections.Select(s => s.Activity.Id).Distinct())
            {
                var tally = TallyFor(activityTallies, activityId);
                tally.Offer(ev.Date);
                tally.Observe(eventMetrics?.Likes ?? 0, eventMetrics?.Attendees);
            }

            // The winner is the Selection whose Activity Id is WinnerActivityId; none matching means no win.
            var winner = ev.WinnerActivityId is { } winnerId ? selections.FirstOrDefault(s => s.Activity.Id == winnerId) : null;
            if (winner is null) continue;
            TallyFor(gameTallies, winner.Game.Id).Wins++;
            TallyFor(activityTallies, winner.Activity.Id).Wins++;
        }

        var libraryGames = new Dictionary<Guid, GameEntity>();
        foreach (var g in games ?? []) libraryGames.TryAdd(g.Id, g);
        var libraryActivities = new Dictionary<Guid, ActivityEntity>();
        foreach (var a in activities ?? []) libraryActivities.TryAdd(a.Id, a);

        string GameName(Guid gameId) =>
            libraryGames.TryGetValue(gameId, out var g) ? g.Name
            : gameSnapshots.TryGetValue(gameId, out var snap) ? snap.Game.Name
            : UnknownGame;

        var gameRows = libraryGames.Keys.Concat(gameSnapshots.Keys.Where(id => !libraryGames.ContainsKey(id)))
            .Select(id =>
            {
                var tally = gameTallies.GetValueOrDefault(id) ?? new Tally();
                var deleted = !libraryGames.ContainsKey(id);
                return new GamePlayStats(id, GameName(id), tally.Offered, tally.Wins, tally.Last, deleted);
            });

        var activityRows = libraryActivities.Keys.Concat(activitySnapshots.Keys.Where(id => !libraryActivities.ContainsKey(id)))
            .Select(id =>
            {
                var tally = activityTallies.GetValueOrDefault(id) ?? new Tally();
                var row = libraryActivities.TryGetValue(id, out var current)
                    ? new ActivityPlayStats(id, current.Name, GameName(current.GameId), tally.Offered, tally.Wins, tally.Last, false)
                    : new ActivityPlayStats(id, activitySnapshots[id].Activity.Name, GameName(activitySnapshots[id].Activity.GameId),
                        tally.Offered, tally.Wins, tally.Last, true);
                return row with
                {
                    WinRate = tally.Offered == 0 ? null : (decimal)tally.Wins / tally.Offered,
                    AverageLikes = tally.Offered == 0 ? null : (decimal)tally.Likes / tally.Offered,
                    AverageAttendees = tally.AttendeeEvents == 0 ? null : (decimal)tally.Attendees / tally.AttendeeEvents,
                };
            });

        return new PlayStatsReport(
            gameRows.OrderBy(r => r.LastOffered).ThenByDescending(r => r.Offered)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id).ToList(),
            activityRows.OrderBy(r => r.LastOffered).ThenByDescending(r => r.Offered)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id).ToList());
    }

    // Keeps the snapshot from the newest Event; on a tie the first one seen stays.
    private static void Remember<T>(Dictionary<Guid, (DateTimeOffset Date, T Item)> snapshots, Guid id, DateTimeOffset date, T item)
    {
        if (!snapshots.TryGetValue(id, out var known) || date > known.Date)
            snapshots[id] = (date, item);
    }

    private static Tally TallyFor(Dictionary<Guid, Tally> tallies, Guid id)
    {
        if (!tallies.TryGetValue(id, out var tally)) tallies[id] = tally = new Tally();
        return tally;
    }

    private sealed class Tally
    {
        public int Offered { get; private set; }
        public int Wins { get; set; }
        public DateTimeOffset? Last { get; private set; }

        // Activity performance only: the Likes of every Event offered, and the attendees of those that have them.
        public long Likes { get; private set; }
        public long Attendees { get; private set; }
        public int AttendeeEvents { get; private set; }

        public void Offer(DateTimeOffset date)
        {
            Offered++;
            if (Last is null || date > Last) Last = date;
        }

        public void Observe(int likes, int? attendees)
        {
            Likes += likes;
            if (attendees is not { } a) return;
            Attendees += a;
            AttendeeEvents++;
        }
    }
}
