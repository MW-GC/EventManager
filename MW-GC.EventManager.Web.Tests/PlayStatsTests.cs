using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Tests;

// Issue #96: the play stats aggregation, through PlayStats.Compute only. "Now" is a fixed clock.
[TestClass]
public sealed class PlayStatsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private readonly PlayStats stats = new(new FixedClock(Now));

    private readonly GameEntity kart = new() { Name = "Kart" };
    private readonly GameEntity puzzle = new() { Name = "Puzzle" };
    private readonly ActivityEntity sprint;
    private readonly ActivityEntity battle;
    private readonly ActivityEntity tiles;

    public PlayStatsTests()
    {
        sprint = new ActivityEntity { Name = "Sprint", GameId = kart.Id };
        battle = new ActivityEntity { Name = "Battle", GameId = kart.Id };
        tiles = new ActivityEntity { Name = "Tiles", GameId = puzzle.Id };
    }

    private static DateTimeOffset Day(int month, int day) => new(2026, month, day, 19, 0, 0, TimeSpan.Zero);

    private static Selection Pick(GameEntity game, ActivityEntity act) => Pick(game.Id, game.Name, act.Id, act.Name);

    private static Selection Pick(Guid gameId, string gameName, Guid activityId, string activityName) => new()
    {
        Game = new Game { Id = gameId, Name = gameName },
        Activity = new Activity { Id = activityId, GameId = gameId, Name = activityName }
    };

    private static EventEntity Event(DateTimeOffset date, Guid? winner, params Selection[] selections) => new()
    {
        Name = "Night " + date.ToString("MM-dd"),
        Date = date,
        UniqueGamesOnly = false,
        Selections = [.. selections],
        WinnerActivityId = winner
    };

    private PlayStatsReport Compute(params EventEntity[] events) => stats.Compute(events, [kart, puzzle], [sprint, battle, tiles]);

    private static GamePlayStats Game(PlayStatsReport report, Guid id) => report.Games.Single(g => g.Id == id);
    private static ActivityPlayStats Activity(PlayStatsReport report, Guid id) => report.Activities.Single(a => a.Id == id);

    [TestMethod]
    public void AGameInTwoSelectionsOfOneEvent_CountsOnceForThatEvent()
    {
        var report = Compute(
            Event(Day(3, 1), sprint.Id, Pick(kart, sprint), Pick(kart, battle)),
            Event(Day(4, 1), null, Pick(kart, battle), Pick(puzzle, tiles)));

        Assert.AreEqual(2, Game(report, kart.Id).Offered);
        Assert.AreEqual(1, Game(report, puzzle.Id).Offered);
        Assert.AreEqual(1, Activity(report, sprint.Id).Offered);
        Assert.AreEqual(2, Activity(report, battle.Id).Offered);
        Assert.AreEqual(Day(4, 1), Game(report, kart.Id).LastOffered);
        Assert.AreEqual(Day(3, 1), Activity(report, sprint.Id).LastOffered);
    }

    [TestMethod]
    public void TheWinningSelection_CountsAWinForItsGameAndItsActivity()
    {
        var report = Compute(
            Event(Day(3, 1), battle.Id, Pick(kart, sprint), Pick(kart, battle), Pick(puzzle, tiles)),
            Event(Day(4, 1), tiles.Id, Pick(kart, sprint), Pick(puzzle, tiles)),
            Event(Day(5, 1), battle.Id, Pick(kart, battle), Pick(puzzle, tiles)));

        Assert.AreEqual(2, Game(report, kart.Id).Wins);
        Assert.AreEqual(1, Game(report, puzzle.Id).Wins);
        Assert.AreEqual(0, Activity(report, sprint.Id).Wins);
        Assert.AreEqual(2, Activity(report, battle.Id).Wins);
        Assert.AreEqual(1, Activity(report, tiles.Id).Wins);
    }

    [TestMethod]
    public void AWinnerThatMatchesNoSelection_CountsNoWin()
    {
        var report = Compute(Event(Day(3, 1), Guid.NewGuid(), Pick(kart, sprint), Pick(puzzle, tiles)));

        Assert.IsTrue(report.Games.All(g => g.Wins == 0));
        Assert.IsTrue(report.Activities.All(a => a.Wins == 0));
        Assert.AreEqual(1, Game(report, kart.Id).Offered);
        Assert.AreEqual(1, Activity(report, tiles.Id).Offered);
    }

    [TestMethod]
    public void EventsWithNoWinner_CountAsOfferedButWinNothing()
    {
        var report = Compute(
            Event(Day(3, 1), null, Pick(kart, sprint), Pick(puzzle, tiles)),
            Event(Day(4, 1), null, Pick(kart, battle)));

        Assert.AreEqual(2, Game(report, kart.Id).Offered);
        Assert.AreEqual(0, Game(report, kart.Id).Wins);
        Assert.AreEqual(0, report.Activities.Sum(a => a.Wins));
    }

    [TestMethod]
    public void FutureEvents_AreIgnored_AndAnEventAtNowCounts()
    {
        var report = Compute(
            Event(Now, sprint.Id, Pick(kart, sprint)),
            Event(Now.AddTicks(1), tiles.Id, Pick(puzzle, tiles)),
            Event(Day(12, 24), battle.Id, Pick(kart, battle)));

        Assert.AreEqual(1, Game(report, kart.Id).Offered);
        Assert.AreEqual(1, Game(report, kart.Id).Wins);
        Assert.AreEqual(Now, Game(report, kart.Id).LastOffered);
        Assert.AreEqual((0, 0, (DateTimeOffset?)null), Measures(Game(report, puzzle.Id)));
        Assert.AreEqual((0, 0, (DateTimeOffset?)null), Measures(Activity(report, battle.Id)));
        Assert.AreEqual((0, 0, (DateTimeOffset?)null), Measures(Activity(report, tiles.Id)));
    }

    [TestMethod]
    public void ADeletedGameAndActivity_StillHaveRows_MarkedDeleted_NamedFromTheNewestSnapshot()
    {
        var goneGame = Guid.NewGuid();
        var goneActivity = Guid.NewGuid();
        var report = Compute(
            Event(Day(3, 1), goneActivity, Pick(goneGame, "Old name", goneActivity, "Old activity")),
            Event(Day(6, 1), null, Pick(goneGame, "Newest name", goneActivity, "Newest activity"), Pick(kart, sprint)),
            Event(Day(4, 1), null, Pick(goneGame, "Middle name", goneActivity, "Middle activity")));

        var game = Game(report, goneGame);
        Assert.IsTrue(game.Deleted);
        Assert.AreEqual("Newest name", game.Name);
        Assert.AreEqual((3, 1, (DateTimeOffset?)Day(6, 1)), Measures(game));

        var activity = Activity(report, goneActivity);
        Assert.IsTrue(activity.Deleted);
        Assert.AreEqual("Newest activity", activity.Name);
        Assert.AreEqual("Newest name", activity.GameName);
        Assert.AreEqual((3, 1, (DateTimeOffset?)Day(6, 1)), Measures(activity));

        Assert.IsFalse(Game(report, kart.Id).Deleted);
        Assert.IsFalse(Activity(report, sprint.Id).Deleted);
    }

    [TestMethod]
    public void ADeletedActivityOfAGameStillInTheLibrary_TakesTheCurrentGameName()
    {
        var goneActivity = Guid.NewGuid();
        var report = Compute(Event(Day(3, 1), null, Pick(kart.Id, "Kart (old name)", goneActivity, "Drift")));

        var activity = Activity(report, goneActivity);
        Assert.IsTrue(activity.Deleted);
        Assert.AreEqual("Drift", activity.Name);
        Assert.AreEqual("Kart", activity.GameName);
        Assert.IsFalse(Game(report, kart.Id).Deleted);
        Assert.AreEqual("Kart", Game(report, kart.Id).Name);
    }

    [TestMethod]
    public void ItemsStillInTheLibrary_TakeTheirCurrentNames_NotTheSnapshot()
    {
        var report = Compute(Event(Day(3, 1), sprint.Id, Pick(kart.Id, "Old kart", sprint.Id, "Old sprint")));

        Assert.AreEqual("Kart", Game(report, kart.Id).Name);
        Assert.AreEqual("Sprint", Activity(report, sprint.Id).Name);
        Assert.AreEqual("Kart", Activity(report, sprint.Id).GameName);
        Assert.AreEqual(2, report.Games.Count);
        Assert.AreEqual(3, report.Activities.Count);
    }

    [TestMethod]
    public void LibraryItemsNeverOffered_AreListedWithZeroZeroAndNever()
    {
        var report = Compute(Event(Day(3, 1), sprint.Id, Pick(kart, sprint)));

        var never = Game(report, puzzle.Id);
        Assert.AreEqual(("Puzzle", false), (never.Name, never.Deleted));
        Assert.AreEqual((0, 0, (DateTimeOffset?)null), Measures(never));
        Assert.AreEqual((0, 0, (DateTimeOffset?)null), Measures(Activity(report, battle.Id)));
        Assert.AreEqual("Kart", Activity(report, battle.Id).GameName);
        Assert.AreEqual((0, 0, (DateTimeOffset?)null), Measures(Activity(report, tiles.Id)));
    }

    [TestMethod]
    public void TheSameNameOnTwoIds_StaysTwoRows()
    {
        var twinGame = new GameEntity { Name = "Kart" };
        var twinActivity = new ActivityEntity { Name = "Sprint", GameId = twinGame.Id };
        var report = stats.Compute(
            [Event(Day(3, 1), sprint.Id, Pick(kart, sprint)), Event(Day(4, 1), null, Pick(twinGame, twinActivity))],
            [kart, twinGame],
            [sprint, twinActivity]);

        var karts = report.Games.Where(g => g.Name == "Kart").ToList();
        Assert.HasCount(2, karts);
        Assert.AreEqual((1, 1, (DateTimeOffset?)Day(3, 1)), Measures(Game(report, kart.Id)));
        Assert.AreEqual((1, 0, (DateTimeOffset?)Day(4, 1)), Measures(Game(report, twinGame.Id)));
        Assert.HasCount(2, report.Activities.Where(a => a.Name == "Sprint"));
        Assert.AreEqual((1, 0, (DateTimeOffset?)Day(4, 1)), Measures(Activity(report, twinActivity.Id)));
    }

    [TestMethod]
    public void TheSameNameOnADeletedId_StaysASeparateRow()
    {
        var oldKart = Guid.NewGuid();
        var report = Compute(Event(Day(3, 1), null, Pick(oldKart, "Kart", Guid.NewGuid(), "Sprint")));

        CollectionAssert.AreEquivalent(new[] { (kart.Id, false), (oldKart, true) },
            report.Games.Where(g => g.Name == "Kart").Select(g => (g.Id, g.Deleted)).ToList());
        Assert.AreEqual(0, Game(report, kart.Id).Offered);
        Assert.HasCount(2, report.Activities.Where(a => a.Name == "Sprint"));
    }

    [TestMethod]
    public void AnEmptyEventsList_ListsTheLibraryWithNothingOffered()
    {
        var report = Compute();

        Assert.HasCount(2, report.Games);
        Assert.HasCount(3, report.Activities);
        Assert.IsTrue(report.Games.All(g => Measures(g) == (0, 0, null) && !g.Deleted));
        Assert.IsTrue(report.Activities.All(a => Measures(a) == (0, 0, null) && !a.Deleted));
    }

    [TestMethod]
    public void NothingAtAll_GivesNoRows()
    {
        var report = stats.Compute([], [], []);
        Assert.IsEmpty(report.Games);
        Assert.IsEmpty(report.Activities);
    }

    [TestMethod]
    public void Rows_ComeNeverOfferedFirst_ThenLongestUnused_ThenMostOffered()
    {
        var mono = new GameEntity { Name = "Mono" };
        var chess = new GameEntity { Name = "Chess" };
        var report = stats.Compute(
            [
                Event(Day(5, 1), null, Pick(kart, sprint)),
                Event(Day(3, 1), null, Pick(puzzle, tiles)),
                Event(Day(5, 1), null, Pick(chess, battle)),
                Event(Day(2, 1), null, Pick(chess, battle)),
            ],
            [kart, puzzle, mono, chess],
            []);

        CollectionAssert.AreEqual(new[] { "Mono", "Puzzle", "Chess", "Kart" }, report.Games.Select(g => g.Name).ToList());
    }

    private static (int, int, DateTimeOffset?) Measures(GamePlayStats g) => (g.Offered, g.Wins, g.LastOffered);
    private static (int, int, DateTimeOffset?) Measures(ActivityPlayStats a) => (a.Offered, a.Wins, a.LastOffered);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
