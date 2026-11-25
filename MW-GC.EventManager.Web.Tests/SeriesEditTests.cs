using System.Text.Json;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Shared.Series;
using MW_GC.EventManager.Web.Services;
using MW_GC.EventManager.Web.Shared.Events;

namespace MW_GC.EventManager.Web.Tests;

// #100, #204 (Web part 2b): the pure part of the edit scope question, without bUnit: what changed, which scopes are
// offered for it, and the Series body a series scope sends, built from the Series as stored.
[TestClass]
public sealed class SeriesEditTests
{
    private static readonly TimeSpan Summer = TimeSpan.FromHours(-4), Winter = TimeSpan.FromHours(-5), Browser = new(5, 45, 0);
    private static readonly Guid Kart = Guid.Parse("11111111-0000-0000-0000-000000000001"), GrandPrix = Guid.Parse("11111111-0000-0000-0000-000000000002");
    private static readonly Guid Brawl = Guid.Parse("22222222-0000-0000-0000-000000000001"), Stock = Guid.Parse("22222222-0000-0000-0000-000000000002");

    // 15 October 2026 19:00 at UTC-04:00, shown in a UTC+05:45 browser as 16 October 04:45.
    private readonly EventEntity occurrence = new()
    {
        Name = "Weekly raid",
        Date = new DateTimeOffset(2026, 10, 15, 19, 0, 0, Summer),
        OccurrenceKey = new DateTimeOffset(2026, 10, 15, 19, 0, 0, Summer),
        SeriesId = Guid.NewGuid(),
        UniqueGamesOnly = true,
        Selections =
        [
            new Selection { Game = new Game { Id = Kart, Name = "Kart" }, Activity = new Activity { Id = GrandPrix, GameId = Kart, Name = "Grand Prix" } },
            new Selection { Game = new Game { Id = Brawl, Name = "Brawl" }, Activity = new Activity { Id = Stock, GameId = Brawl, Name = "Stock" } },
        ],
    };

    private OccurrenceEdits Unchanged() => new(occurrence.Name, occurrence.Date.ToOffset(Browser), true, [(Kart, GrandPrix), (Brawl, Stock)]);

    [TestMethod]
    public void NothingChanged_IsNoChange_EvenAtTheBrowsersOffset() => Assert.IsFalse(SeriesEdit.Compare(occurrence, Unchanged()).Any);

    [TestMethod]
    [DataRow("name", true)]
    [DataRow("time", true)]
    [DataRow("unique games", true)]
    [DataRow("name and time", true)]
    [DataRow("day", false)]
    [DataRow("day and time", false)]
    [DataRow("removed slot", false)]
    [DataRow("added slot", false)]
    [DataRow("replaced activity", false)]
    [DataRow("swapped slots", false)]
    [DataRow("name and slot", false)]
    public void EachKindOfChange_OffersItsScopes(string change, bool seriesWide)
    {
        var edits = Unchanged();
        if (change.Contains("name")) edits = edits with { Name = "Raid night" };
        if (change.Contains("time")) edits = edits with { Date = edits.Date.AddHours(1) };
        if (change.Contains("day")) edits = edits with { Date = edits.Date.AddDays(1) };
        if (change == "unique games") edits = edits with { UniqueGames = false };
        if (change == "removed slot") edits = edits with { Slots = [(Kart, GrandPrix)] };
        if (change == "added slot") edits = edits with { Slots = [(Kart, GrandPrix), (Brawl, Stock), (Brawl, Guid.NewGuid())] };
        if (change == "replaced activity") edits = edits with { Slots = [(Kart, GrandPrix), (Brawl, Guid.NewGuid())] };
        if (change == "swapped slots") edits = edits with { Slots = [(Brawl, Stock), (Kart, GrandPrix)] };
        if (change.Contains("slot") && change.StartsWith("name")) edits = edits with { Slots = [(Kart, GrandPrix)] };

        var found = SeriesEdit.Compare(occurrence, edits);
        var question = SeriesEdit.Question(found, occurrence.Name, occurrence.Date, Nothing, Nothing, Nothing);

        Assert.IsTrue(found.Any);
        Assert.AreEqual(seriesWide, found.SeriesWide);
        Assert.IsFalse(question.Destructive);
        Assert.AreEqual(("Update repeating event", "What to update"), (question.Title, question.GroupLabel));
        if (seriesWide)
        {
            CollectionAssert.AreEqual(new[] { "This event only", "This and following events", "All future events" }, question.Options.Select(o => o.Label).ToList());
            CollectionAssert.AreEqual(new[] { "Update this event", "Update this and following", "Update all future events" }, question.Options.Select(o => o.ConfirmText).ToList());
            StringAssert.StartsWith(question.Message, "Weekly raid on ");
        }
        else
        {
            Assert.AreEqual("Update this event", Assert.ContainsSingle(question.Options).ConfirmText);
            Assert.AreEqual("A changed date or changed activities apply to this event only.", question.Message);
        }
    }

    // Each option runs its own action.
    [TestMethod]
    public async Task EachOption_RunsItsOwnAction()
    {
        var ran = new List<string>();
        Func<Task> Run(string name) => () => { ran.Add(name); return Task.CompletedTask; };
        var question = SeriesEdit.Question(new OccurrenceChange(true, false, false, false, false), "x", default, Run("one"), Run("following"), Run("all"));

        foreach (var option in question.Options) await option.OnConfirm();

        CollectionAssert.AreEqual(new[] { "one", "following", "all" }, ran);
    }

    // The body keeps every stored field the admin cannot change here, each distinct and set.
    [TestMethod]
    [DataRow(SeriesScope.All)]
    [DataRow(SeriesScope.Following)]
    public void TheBody_IsTheStoredSeries_WithOnlyTheNameTimeAndUniqueGamesReplaced(SeriesScope scope)
    {
        var stored = Stored(new DateTimeOffset(2026, 9, 24, 19, 0, 0, Summer));
        var edits = Unchanged() with { Name = "Raid night", UniqueGames = false, Date = new DateTimeOffset(2026, 10, 16, 5, 45, 0, Browser) };

        var body = SeriesEdit.Body(stored, occurrence, edits, scope);

        Assert.AreEqual(("Raid night", false), (body.Name, body.UniqueGamesOnly));
        Assert.AreEqual(scope == SeriesScope.All ? new DateTimeOffset(2026, 9, 24, 20, 0, 0, Summer) : new DateTimeOffset(2026, 10, 15, 20, 0, 0, Summer), body.Schedule.First);
        Assert.AreEqual(Summer, body.Schedule.First.Offset);
        Assert.AreEqual((stored.Id, stored.ETag, stored.PartitionKey), (body.Id, body.ETag, body.PartitionKey));
        Assert.AreEqual(stored.Schedule.Rule, body.Schedule.Rule);
        Assert.AreEqual(stored.Schedule.End, body.Schedule.End);
        Assert.AreEqual(stored.SlotCount, body.SlotCount);
        Assert.AreEqual(JsonSerializer.Serialize(stored.Filters), JsonSerializer.Serialize(body.Filters));
        CollectionAssert.AreEqual(stored.Skipped, body.Skipped);
        Assert.AreEqual((stored.SplitAt, stored.SplitFrom), (body.SplitAt, body.SplitFrom));
    }

    [TestMethod]
    public void AnUnchangedNameTimeOrUniqueGames_GoesBackAsStored()
    {
        // The stored Series' name and Unique games differ from the Occurrence's: a renamed Occurrence (Modified earlier).
        var stored = Stored(new DateTimeOffset(2026, 9, 24, 18, 30, 0, Summer));
        stored.Name = "Series name";
        stored.UniqueGamesOnly = false;

        var body = SeriesEdit.Body(stored, occurrence, Unchanged() with { Slots = [] }, SeriesScope.All);

        Assert.AreEqual(("Series name", false), (body.Name, body.UniqueGamesOnly));
        Assert.AreEqual(stored.Schedule.First, body.Schedule.First);
    }

    // A Series stored at UTC-05:00 (winter) edited from an Occurrence shown at UTC-04:00 (summer) in a UTC-04:00 browser:
    // 20:00 typed in the browser is 19:00 at the Series' offset, and the start stays at UTC-05:00.
    [TestMethod]
    public void TheTimeOfDay_IsReadAtTheStoredOffset_AcrossADaylightSavingOffset()
    {
        var stored = Stored(new DateTimeOffset(2026, 1, 8, 18, 0, 0, Winter));
        var summer = new EventEntity { Name = "Raid", Date = new DateTimeOffset(2026, 7, 9, 18, 0, 0, Winter).ToOffset(Summer), OccurrenceKey = new DateTimeOffset(2026, 7, 9, 18, 0, 0, Winter), SeriesId = stored.Id };
        var edits = new OccurrenceEdits("Raid", new DateTimeOffset(2026, 7, 9, 20, 0, 0, Summer), true, []);

        Assert.AreEqual(new DateTimeOffset(2026, 1, 8, 19, 0, 0, Winter), SeriesEdit.Body(stored, summer, edits, SeriesScope.All).Schedule.First);
        Assert.AreEqual(new DateTimeOffset(2026, 7, 9, 19, 0, 0, Winter), SeriesEdit.Body(stored, summer, edits, SeriesScope.Following).Schedule.First);
        Assert.IsNull(SeriesEdit.OtherDay(stored, summer, edits));
    }

    // "This and following" starts on the day of the Occurrence's key at the Series' offset, even when its Date was moved
    // by hand to another day earlier.
    [TestMethod]
    public void Following_StartsOnTheKeysDay_NotAMovedDate()
    {
        var stored = Stored(new DateTimeOffset(2026, 9, 24, 19, 0, 0, Summer));
        var moved = new EventEntity { Name = "Raid", Date = new DateTimeOffset(2026, 10, 17, 19, 0, 0, Summer), OccurrenceKey = new DateTimeOffset(2026, 10, 15, 19, 0, 0, Summer), SeriesId = stored.Id };

        var body = SeriesEdit.Body(stored, moved, new OccurrenceEdits("Raid 2", moved.Date, true, []), SeriesScope.Following);

        Assert.AreEqual(new DateTimeOffset(2026, 10, 15, 19, 0, 0, Summer), body.Schedule.First);
    }

    // An Occurrence at 21:00 on 15 October at the Series' UTC-04:00 is 01:00 on 16 October in UTC (and 06:45 on 16
    // October in the browser): "This and following" starts on 15 October, the key's day at the Series' own offset.
    [TestMethod]
    [DataRow("name")]
    [DataRow("time")]
    public void Following_StartsOnTheKeysDayAtTheSeriesOffset_NotItsUtcDay(string change)
    {
        var stored = Stored(new DateTimeOffset(2026, 9, 24, 21, 0, 0, Summer));
        var key = new DateTimeOffset(2026, 10, 15, 21, 0, 0, Summer);
        var late = new EventEntity { Name = "Raid", Date = key, OccurrenceKey = key, SeriesId = stored.Id };
        // 22:00 on 15 October at UTC-04:00 is 07:45 on 16 October in the browser.
        var edits = change == "name"
            ? new OccurrenceEdits("Raid 2", key.ToOffset(Browser), true, [])
            : new OccurrenceEdits("Raid", new DateTimeOffset(2026, 10, 16, 7, 45, 0, Browser), true, []);

        var body = SeriesEdit.Body(stored, late, edits, SeriesScope.Following);

        Assert.AreEqual(new DateTimeOffset(2026, 10, 15, change == "name" ? 21 : 22, 0, 0, Summer), body.Schedule.First);
        Assert.AreEqual(Summer, body.Schedule.First.Offset);
        Assert.IsNull(SeriesEdit.OtherDay(stored, late, edits));
    }

    [TestMethod]
    [DataRow(11, 0, true)]
    [DataRow(4, 0, false)]
    [DataRow(9, 44, false)]
    [DataRow(9, 45, true)]
    public void ATimeOnAnotherDayAtTheSeriesOffset_IsRefused(int hour, int minute, bool refused)
    {
        // 16 October in the UTC+05:45 browser: before 09:45 it is still 15 October at UTC-04:00.
        var stored = Stored(new DateTimeOffset(2026, 9, 24, 19, 0, 0, Summer));
        var edits = Unchanged() with { Date = new DateTimeOffset(2026, 10, 16, hour, minute, 0, Browser) };

        Assert.AreEqual(refused ? SeriesEdit.OtherDayMessage : null, SeriesEdit.OtherDay(stored, occurrence, edits));
    }

    private static Task Nothing() => Task.CompletedTask;

    private static SeriesEntity Stored(DateTimeOffset first) => new()
    {
        Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
        PartitionKey = "Series",
        ETag = new Azure.ETag("W/\"datetime'2026-11-25T10%3A00%3A00Z'\""),
        Name = "Weekly raid",
        Schedule = new RecurrenceSeries(first, RecurrenceRule.MonthlyNthWeekday(WeekOfMonth.Third, DayOfWeek.Thursday), first.AddYears(1)),
        SlotCount = 4,
        UniqueGamesOnly = true,
        Filters = new SeriesFilters { ThemedOnly = true, GameIds = [Kart], ThemeIds = [Guid.NewGuid()], HolidayIds = [Guid.NewGuid()] },
        Skipped = [first.AddDays(14)],
        SplitAt = first.AddMonths(6),
        SplitFrom = first.AddDays(-7),
    };
}
