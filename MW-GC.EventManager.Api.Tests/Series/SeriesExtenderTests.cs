using Microsoft.AspNetCore.Http;
using static MW_GC.EventManager.Api.Tests.Series.SeriesExtenderSupport;
using static MW_GC.EventManager.Api.Tests.Series.SeriesHarness;
using static MW_GC.EventManager.Api.Tests.Series.SeriesTestSupport;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// The daily run (#204) extends every Series with the same generator a create uses: it adds the Occurrences
/// the moving horizon now reaches, and nothing else. It never makes a cancelled one again, never touches an
/// Occurrence that is stored already, and never writes the Series row.
/// </summary>
[TestClass]
public sealed class SeriesExtenderTests
{
    private readonly SeriesHarness h = new();

    private int EventInserts() => h.EventTable.Invocations.Count(i => i.Method.Name == nameof(Azure.Data.Tables.TableClient.AddEntityAsync));

    [TestMethod]
    public async Task A_weekly_Series_created_eight_weeks_ago_gets_the_eight_weeks_the_horizon_moved_over_and_only_those()
    {
        var (id, _, _) = await h.CreateAsync();
        var created = Dates(await h.OccurrencesAsync(id));
        h.Clock.Now = At("2027-04-29T12:00:00-05:00");

        var run = await Extender(h).ExtendAllAsync(h.Clock, default);

        Assert.AreEqual(8, run.Created);
        Assert.AreEqual(1, run.Seen);
        Assert.IsEmpty(run.Failed);
        string[] added =
        [
            "2027-04-29T19:00:00-05:00", "2027-05-06T19:00:00-05:00", "2027-05-13T19:00:00-05:00", "2027-05-20T19:00:00-05:00",
            "2027-05-27T19:00:00-05:00", "2027-06-03T19:00:00-05:00", "2027-06-10T19:00:00-05:00", "2027-06-17T19:00:00-05:00",
        ];
        CollectionAssert.AreEqual(created.Concat(added).ToArray(), Dates(await h.OccurrencesAsync(id)));
        foreach (var occurrence in (await h.OccurrencesAsync(id)).Where(o => added.Contains(Local(o.Date))))
        {
            Assert.IsEmpty(occurrence.Selections);
            Assert.AreEqual("Thursday night", occurrence.Name);
            Assert.AreEqual(occurrence.Date, occurrence.OccurrenceKey);
            Assert.AreEqual(id, occurrence.SeriesId);
            Assert.IsFalse(occurrence.Modified);
        }
    }

    [TestMethod]
    public async Task Each_run_adds_only_the_week_the_horizon_reached_since_the_last()
    {
        var (id, _, _) = await h.CreateAsync();

        // The horizon ends 8 weeks after now: at 18:00 on 29 April it misses that day's 19:00 Occurrence.
        h.Clock.Now = At("2027-03-04T18:00:00-05:00");
        Assert.AreEqual(0, (await Extender(h).ExtendAllAsync(h.Clock, default)).Created);
        Assert.HasCount(8, await h.OccurrencesAsync(id));

        h.Clock.Now = At("2027-03-05T12:00:00-05:00");
        Assert.AreEqual(1, (await Extender(h).ExtendAllAsync(h.Clock, default)).Created);
        Assert.AreEqual("2027-04-29T19:00:00-05:00", Dates(await h.OccurrencesAsync(id)).Last());

        h.Clock.Now = At("2027-03-11T12:00:00-05:00");
        Assert.AreEqual(0, (await Extender(h).ExtendAllAsync(h.Clock, default)).Created);

        h.Clock.Now = At("2027-03-12T12:00:00-05:00");
        Assert.AreEqual(1, (await Extender(h).ExtendAllAsync(h.Clock, default)).Created);
        Assert.AreEqual("2027-05-06T19:00:00-05:00", Dates(await h.OccurrencesAsync(id)).Last());
        Assert.HasCount(10, await h.OccurrencesAsync(id));
    }

    [TestMethod]
    public async Task A_second_run_creates_nothing_and_tries_no_insert()
    {
        var (id, _, _) = await h.CreateAsync();
        h.Clock.Now = At("2027-03-25T12:00:00-05:00");
        Assert.AreEqual(3, (await Extender(h).ExtendAllAsync(h.Clock, default)).Created);
        var rows = Snapshot(h.Events);
        var inserts = EventInserts();

        var again = await Extender(h).ExtendAllAsync(h.Clock, default);

        Assert.AreEqual(0, again.Created);
        Assert.AreEqual(inserts, EventInserts());
        CollectionAssert.AreEquivalent(rows, Snapshot(h.Events));
        Assert.HasCount(11, await h.OccurrencesAsync(id));
    }

    [TestMethod]
    public async Task A_Series_whose_end_has_passed_gets_nothing()
    {
        var (id, _, _) = await h.CreateAsync(Body(end: "2027-04-01T19:00:00-05:00"));
        Assert.HasCount(5, await h.OccurrencesAsync(id));
        h.Clock.Now = At("2027-05-01T12:00:00-05:00");
        var inserts = EventInserts();

        var run = await Extender(h).ExtendAllAsync(h.Clock, default);

        Assert.AreEqual(0, run.Created);
        Assert.AreEqual(1, run.Seen);
        Assert.AreEqual(inserts, EventInserts());
        Assert.HasCount(5, await h.OccurrencesAsync(id));
    }

    [TestMethod]
    public async Task A_Series_whose_end_falls_inside_the_horizon_is_extended_up_to_its_end()
    {
        var (id, _, _) = await h.CreateAsync(Body(end: "2027-05-06T19:00:00-05:00"));
        h.Clock.Now = At("2027-04-01T12:00:00-05:00");

        var run = await Extender(h).ExtendAllAsync(h.Clock, default);

        Assert.AreEqual(2, run.Created);
        CollectionAssert.AreEqual(new[] { "2027-04-29T19:00:00-05:00", "2027-05-06T19:00:00-05:00" },
            Dates(await h.OccurrencesAsync(id)).TakeLast(2).ToArray());
    }

    [TestMethod]
    public async Task A_yearly_Series_keeps_at_least_the_next_one_however_far_out_it_is()
    {
        var (id, _, _) = await h.CreateAsync(Body(first: "2027-12-25T19:00:00-05:00", rule: new { kind = "AnnualSameDate" }));
        CollectionAssert.AreEqual(new[] { "2027-12-25T19:00:00-05:00" }, Dates(await h.OccurrencesAsync(id)));

        // Its one Occurrence is still ahead and nothing else is in the horizon: nothing to add.
        h.Clock.Now = At("2027-11-20T12:00:00-05:00");
        Assert.AreEqual(0, (await Extender(h).ExtendAllAsync(h.Clock, default)).Created);

        // Once it has passed, the next one is made, a year out.
        h.Clock.Now = At("2027-12-26T12:00:00-05:00");
        Assert.AreEqual(1, (await Extender(h).ExtendAllAsync(h.Clock, default)).Created);
        CollectionAssert.AreEqual(new[] { "2027-12-25T19:00:00-05:00", "2028-12-25T19:00:00-05:00" }, Dates(await h.OccurrencesAsync(id)));
    }

    [TestMethod]
    public async Task A_cancelled_Occurrence_is_not_made_again()
    {
        var (id, _, _) = await h.CreateAsync();
        var third = (await h.OccurrencesAsync(id))[2];
        Assert.AreEqual(StatusCodes.Status204NoContent, ApiResults.Status(await h.EventApi.Delete(Bare(), third.Id, default)));
        h.Clock.Now = At("2027-03-05T12:00:00-05:00");

        var run = await Extender(h).ExtendAllAsync(h.Clock, default);

        Assert.AreEqual(1, run.Created);
        var dates = Dates(await h.OccurrencesAsync(id));
        CollectionAssert.DoesNotContain(dates, "2027-03-18T19:00:00-05:00");
        CollectionAssert.Contains(dates, "2027-04-29T19:00:00-05:00");
        Assert.HasCount(8, dates);
        CollectionAssert.Contains((await h.SeriesAsync(id))!.Skipped, third.OccurrenceKey!.Value);
    }

    [TestMethod]
    public async Task Modified_played_and_past_Occurrences_stay_exactly_as_stored()
    {
        var (id, _, _) = await h.CreateAsync();
        var occurrences = await h.OccurrencesAsync(id);
        await h.EditOneAsync(occurrences[3], "Special");   // Modified, still ahead after the clock moves
        h.SeedWinner(occurrences[4].Id);                     // played, still ahead
        h.SeedComment(occurrences[5].Id);                    // played through a Comment
        h.SeedMetrics(occurrences[6].Id);                    // played through Event metrics
        h.Clock.Now = At("2027-03-19T12:00:00-05:00");       // the first three are past
        var before = Snapshot(h.Events);

        var run = await Extender(h).ExtendAllAsync(h.Clock, default);

        Assert.AreEqual(3, run.Created);
        var after = Snapshot(h.Events);
        foreach (var (key, row) in before)
            Assert.AreEqual(row, after[key]);
        Assert.AreEqual(before.Count + 3, after.Count);
        Assert.AreEqual("Special", (await h.OccurrencesAsync(id))[3].Name);
    }

    [TestMethod]
    public async Task The_Series_row_is_never_written()
    {
        var (id, _, _) = await h.CreateAsync();
        var second = (await h.OccurrencesAsync(id))[1];
        await h.EventApi.Delete(Bare(), second.Id, default);
        var series = Snapshot(h.SeriesRows);
        var writes = Writes(h.SeriesTable);
        h.Clock.Now = At("2027-03-25T12:00:00-05:00");

        var run = await Extender(h).ExtendAllAsync(h.Clock, default);

        Assert.AreEqual(3, run.Created);
        Assert.AreEqual(writes, Writes(h.SeriesTable));
        CollectionAssert.AreEquivalent(series, Snapshot(h.SeriesRows));
    }

    [TestMethod]
    public async Task Every_Series_is_extended_in_one_run()
    {
        var (thursday, _, _) = await h.CreateAsync();
        var (monthly, _, _) = await h.CreateAsync(Body(name: "Second Tuesday", first: "2027-03-09T20:00:00-05:00",
            rule: new { kind = "MonthlyNthWeekday", week = "Second", weekday = "Tuesday" }));
        Assert.HasCount(2, await h.OccurrencesAsync(monthly));
        h.Clock.Now = At("2027-04-15T12:00:00-05:00");

        var run = await Extender(h).ExtendAllAsync(h.Clock, default);

        Assert.AreEqual(2, run.Seen);
        Assert.AreEqual(6 + 2, run.Created);
        Assert.AreEqual("2027-06-03T19:00:00-05:00", Dates(await h.OccurrencesAsync(thursday)).Last());
        CollectionAssert.AreEqual(
            new[] { "2027-03-09T20:00:00-05:00", "2027-04-13T20:00:00-05:00", "2027-05-11T20:00:00-05:00", "2027-06-08T20:00:00-05:00" },
            Dates(await h.OccurrencesAsync(monthly)));
    }
}
