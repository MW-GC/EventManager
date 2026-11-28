using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Tests;

// Issue #96: the play stats aggregation, through PlayStats.Compute only. "Now" is a fixed clock. The library, the
// Event builder and the report lookups shared by PlayStatsTests (offered, wins, names, order) and
// PlayStatsActivityPerformanceTests (#136: win rate, average Likes and average attendees).
public abstract class PlayStatsFixture
{
    protected static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    protected readonly PlayStats stats = new(new FixedClock(Now));

    protected readonly GameEntity kart = new() { Name = "Kart" };
    protected readonly GameEntity puzzle = new() { Name = "Puzzle" };
    protected readonly ActivityEntity sprint;
    protected readonly ActivityEntity battle;
    protected readonly ActivityEntity tiles;

    protected PlayStatsFixture()
    {
        sprint = new ActivityEntity { Name = "Sprint", GameId = kart.Id };
        battle = new ActivityEntity { Name = "Battle", GameId = kart.Id };
        tiles = new ActivityEntity { Name = "Tiles", GameId = puzzle.Id };
    }

    protected static DateTimeOffset Day(int month, int day) => new(2026, month, day, 19, 0, 0, TimeSpan.Zero);

    protected static Selection Pick(GameEntity game, ActivityEntity act) => Pick(game.Id, game.Name, act.Id, act.Name);

    protected static Selection Pick(Guid gameId, string gameName, Guid activityId, string activityName) => new()
    {
        Game = new Game { Id = gameId, Name = gameName },
        Activity = new Activity { Id = activityId, GameId = gameId, Name = activityName }
    };

    protected static EventEntity Event(DateTimeOffset date, Guid? winner, params Selection[] selections) => new()
    {
        Name = "Night " + date.ToString("MM-dd"),
        Date = date,
        UniqueGamesOnly = false,
        Selections = [.. selections],
        WinnerActivityId = winner
    };

    protected PlayStatsReport Compute(params EventEntity[] events) => stats.Compute(events, [kart, puzzle], [sprint, battle, tiles], []);

    protected PlayStatsReport Compute(EventMetricsWithLikes[] metrics, params EventEntity[] events) =>
        stats.Compute(events, [kart, puzzle], [sprint, battle, tiles], metrics);

    // One Event's metrics item: its attendees (null for cleared) and its Like count.
    protected static EventMetricsWithLikes Metrics(EventEntity ev, int? attendees, int likes) => new(ev.Id, attendees, null, null, null, likes);

    protected static GamePlayStats Game(PlayStatsReport report, Guid id) => report.Games.Single(g => g.Id == id);
    protected static ActivityPlayStats Activity(PlayStatsReport report, Guid id) => report.Activities.Single(a => a.Id == id);

    protected static (int, int, DateTimeOffset?) Measures(GamePlayStats g) => (g.Offered, g.Wins, g.LastOffered);
    protected static (int, int, DateTimeOffset?) Measures(ActivityPlayStats a) => (a.Offered, a.Wins, a.LastOffered);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
