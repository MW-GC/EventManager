using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Shared.Events;

namespace MW_GC.EventManager.Web.Tests;

// #97: the announcement text, through AnnouncementText.For only. Every expected string is written out by hand,
// including each Discord timestamp's UNIX second (worked out apart from the code).
[TestClass]
public sealed class AnnouncementTextTests
{
    // 2026-12-04 19:00:00 UTC is UNIX second 1796410800.
    private static readonly DateTimeOffset Friday = new(2026, 12, 4, 19, 0, 0, TimeSpan.Zero);

    private static Selection Pick(string game, string activity, int? min = null, int? max = null, int? minutes = null)
    {
        var gameId = Guid.NewGuid();
        return new Selection
        {
            Game = new Game { Id = gameId, Name = game },
            Activity = new Activity { GameId = gameId, Name = activity, MinPlayers = min, MaxPlayers = max, DurationMinutes = minutes }
        };
    }

    private static EventEntity Event(string name, DateTimeOffset date, params Selection[] selections) => new()
    {
        Name = name,
        Date = date,
        UniqueGamesOnly = false,
        Selections = [.. selections]
    };

    // ---- the layout ---------------------------------------------------------------------------

    [TestMethod]
    public void FiveSelections_OneLineEach_InTheEventsOrder()
    {
        var e = Event("Game night", Friday,
            Pick("Kart", "Grand Prix"),
            Pick("Brawl", "Stock Battle"),
            Pick("Puzzle", "Tiles"),
            Pick("Kart", "Time Trial"),
            Pick("Quiz", "Lightning Round"));

        Assert.AreEqual(
            "**Game night**\n" +
            "<t:1796410800:F>\n" +
            "- Kart: Grand Prix\n" +
            "- Brawl: Stock Battle\n" +
            "- Puzzle: Tiles\n" +
            "- Kart: Time Trial\n" +
            "- Quiz: Lightning Round",
            AnnouncementText.For(e));
    }

    [TestMethod]
    public void TheWinnersLine_AndOnlyIt_IsMarked()
    {
        var e = Event("Game night", Friday, Pick("Kart", "Grand Prix"), Pick("Brawl", "Stock Battle"), Pick("Puzzle", "Tiles"));
        e.WinnerActivityId = e.Selections[1].Activity.Id;

        Assert.AreEqual(
            "**Game night**\n" +
            "<t:1796410800:F>\n" +
            "- Kart: Grand Prix\n" +
            "- Brawl: Stock Battle 🏆 Winner\n" +
            "- Puzzle: Tiles",
            AnnouncementText.For(e));
    }

    // A Winner id that matches no Selection (never saved so, but the text must not invent one) marks no line.
    [TestMethod]
    public void AWinnerIdOfNoSelection_MarksNoLine()
    {
        var e = Event("Game night", Friday, Pick("Kart", "Grand Prix"), Pick("Brawl", "Stock Battle"));
        e.WinnerActivityId = Guid.NewGuid();

        Assert.AreEqual("**Game night**\n<t:1796410800:F>\n- Kart: Grand Prix\n- Brawl: Stock Battle", AnnouncementText.For(e));
    }

    // The players and duration the Selection snapshot carries, in the dialog's own wording (PlayersDurationText).
    [TestMethod]
    [DataRow(null, null, null, "- Kart: Grand Prix")]
    [DataRow(2, null, null, "- Kart: Grand Prix (2 or more players)")]
    [DataRow(null, 6, null, "- Kart: Grand Prix (up to 6 players)")]
    [DataRow(2, 6, null, "- Kart: Grand Prix (2-6 players)")]
    [DataRow(4, 4, null, "- Kart: Grand Prix (4 players)")]
    [DataRow(null, 1, null, "- Kart: Grand Prix (up to 1 player)")]
    [DataRow(null, null, 45, "- Kart: Grand Prix (45 min)")]
    [DataRow(2, 6, 45, "- Kart: Grand Prix (2-6 players, 45 min)")]
    public void PlayersAndDuration_WhenTheSnapshotCarriesThem(int? min, int? max, int? minutes, string line)
    {
        var e = Event("Game night", Friday, Pick("Kart", "Grand Prix", min, max, minutes));

        Assert.AreEqual("**Game night**\n<t:1796410800:F>\n" + line, AnnouncementText.For(e));
    }

    [TestMethod]
    public void TheWinnersLine_KeepsItsPlayersAndDuration()
    {
        var e = Event("Game night", Friday, Pick("Kart", "Grand Prix", 2, 6, 45), Pick("Brawl", "Stock Battle"));
        e.WinnerActivityId = e.Selections[0].Activity.Id;

        Assert.AreEqual(
            "**Game night**\n<t:1796410800:F>\n- Kart: Grand Prix (2-6 players, 45 min) 🏆 Winner\n- Brawl: Stock Battle",
            AnnouncementText.For(e));
    }

    // Names only: an Activity's description, rules and setup text never reach the text.
    [TestMethod]
    public void NamesOnly_NoDescriptionRulesOrSetup()
    {
        var gameId = Guid.NewGuid();
        var e = Event("Game night", Friday, new Selection
        {
            Game = new Game { Id = gameId, Name = "Kart", Website = "https://kart.example" },
            Activity = new Activity { GameId = gameId, Name = "Grand Prix", Description = "Four cups", Rules = "No items", SetupRequirements = "Four pads" }
        });

        Assert.AreEqual("**Game night**\n<t:1796410800:F>\n- Kart: Grand Prix", AnnouncementText.For(e));
    }

    // An Occurrence not rolled yet: one fixed line, and no throw.
    [TestMethod]
    public void NoSelections_SaysThePicksAreStillToCome()
    {
        var e = Event("Weekly night", Friday);

        Assert.AreEqual("**Weekly night**\n<t:1796410800:F>\nThe picks are still to come.", AnnouncementText.For(e));
    }

    // The line reserved for a future link (#9) is empty: nothing after the last line, not even a line break.
    [TestMethod]
    public void TheTextEndsOnItsLastLine()
    {
        var withSelections = AnnouncementText.For(Event("Game night", Friday, Pick("Kart", "Grand Prix")));
        var without = AnnouncementText.For(Event("Game night", Friday));

        Assert.IsTrue(withSelections.EndsWith("- Kart: Grand Prix", StringComparison.Ordinal), withSelections);
        Assert.IsTrue(without.EndsWith("The picks are still to come.", StringComparison.Ordinal), without);
        Assert.DoesNotContain("\r", withSelections);
        Assert.DoesNotContain("\n\n", withSelections);
    }

    // ---- the Discord timestamp ----------------------------------------------------------------

    // The Event's instant, whole UNIX seconds, whatever offset it was stored with; Discord shows each reader their own zone.
    [TestMethod]
    [DataRow(0, 0, 1796410800L)]   // 19:00 UTC
    [DataRow(-5, 0, 1796428800L)]  // 19:00 at UTC-5 is 00:00 UTC on 5 December
    [DataRow(9, 30, 1796376600L)]  // 19:00 at UTC+9:30 is 09:30 UTC
    public void TheTimestamp_IsTheInstantInUnixSeconds(int hours, int minutes, long unix)
    {
        var date = new DateTimeOffset(2026, 12, 4, 19, 0, 0, new TimeSpan(hours, minutes, 0));

        var text = AnnouncementText.For(Event("Game night", date, Pick("Kart", "Grand Prix")));

        Assert.AreEqual($"<t:{unix}:F>", text.Split('\n')[1]);
    }

    // Fractional seconds are dropped, never rounded up and never written as milliseconds.
    [TestMethod]
    public void TheTimestamp_DropsFractionalSeconds()
    {
        var date = new DateTimeOffset(2026, 12, 4, 19, 0, 0, 999, TimeSpan.Zero);

        var text = AnnouncementText.For(Event("Game night", date, Pick("Kart", "Grand Prix")));

        Assert.AreEqual("<t:1796410800:F>", text.Split('\n')[1]);
    }
}
