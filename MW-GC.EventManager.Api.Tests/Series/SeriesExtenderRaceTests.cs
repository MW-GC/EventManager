using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using MW_GC.EventManager.API.Services;
using static MW_GC.EventManager.Api.Tests.Series.SeriesExtenderSupport;
using static MW_GC.EventManager.Api.Tests.Series.SeriesHarness;
using static MW_GC.EventManager.Api.Tests.Series.SeriesTestSupport;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// Writes that land while the daily run (#204) works: another run on a second instance, or another writer that
/// stores the same Occurrence first. The Occurrence id is derived from the Series and its key and created with
/// the atomic add, so the run makes no twin and overwrites nothing.
/// </summary>
[TestClass]
public sealed class SeriesExtenderRaceTests
{
    private readonly SeriesHarness h = new();

    [TestMethod]
    public async Task An_Occurrence_stored_by_another_writer_first_is_not_overwritten_and_not_counted()
    {
        var (id, _, _) = await h.CreateAsync();
        h.Clock.Now = At("2027-03-12T12:00:00-05:00");
        var due = SeriesOccurrences.OccurrenceId(id, At("2027-04-29T19:00:00-05:00")).ToString("D");
        TableEntity? theirs = null;
        string? stored = null;
        // Right before the run inserts the 29 April Occurrence, another writer stores that row, renamed since.
        h.FailEventWrite = (write, rowKey) =>
        {
            if (write != "add" || rowKey != due || theirs is not null) return false;
            theirs = new TableEntity(TableNames.EventPartition, due)
            {
                ["Name"] = "Stored first", ["Date"] = At("2027-04-29T19:00:00-05:00"), ["SeriesId"] = id,
                ["OccurrenceKey"] = At("2027-04-29T19:00:00-05:00"), ["Selections"] = "[]", ETag = TableMocks.NextETag(),
            };
            h.Events[due] = theirs;
            stored = Snapshot(theirs);
            return false;
        };

        var run = await Extender(h).ExtendAllAsync(h.Clock, default);

        Assert.IsNotNull(theirs);
        Assert.AreEqual(1, run.Created, "only 6 May is this run's");
        Assert.IsEmpty(run.Failed);
        Assert.AreEqual(stored, Snapshot(h.Events[due]));
        var occurrences = await h.OccurrencesAsync(id);
        Assert.HasCount(10, occurrences);
        Assert.AreEqual(1, occurrences.Count(o => Local(o.Date) == "2027-04-29T19:00:00-05:00"));
        Assert.AreEqual("Stored first", occurrences.Single(o => o.Id.ToString("D") == due).Name);
    }

    [TestMethod]
    public async Task Two_runs_that_overlap_make_each_Occurrence_once()
    {
        var (id, _, _) = await h.CreateAsync();
        h.Clock.Now = At("2027-03-25T12:00:00-05:00");
        var interloper = Extender(h);
        SeriesExtension? theirs = null;
        // After this run has read the Occurrences and before its first insert, a run on another instance does the
        // whole job.
        h.FailEventWrite = (write, _) =>
        {
            if (write != "add" || theirs is not null) return false;
            theirs = new SeriesExtension(0, 0, []); // started: its own inserts must not start a third run
            theirs = interloper.ExtendAllAsync(h.Clock, default).GetAwaiter().GetResult();
            return false;
        };

        var ours = await Extender(h).ExtendAllAsync(h.Clock, default);

        Assert.IsNotNull(theirs);
        Assert.AreEqual(3, theirs.Created);
        Assert.AreEqual(0, ours.Created);
        Assert.IsEmpty(ours.Failed);
        var dates = Dates(await h.OccurrencesAsync(id));
        Assert.HasCount(11, dates);
        Assert.HasCount(11, dates.Distinct().ToArray());
        Assert.AreEqual("2027-05-13T19:00:00-05:00", dates.Last());
    }

    [TestMethod]
    public async Task A_run_overlapping_a_repeated_create_of_the_same_Series_makes_each_Occurrence_once()
    {
        var key = Guid.NewGuid();
        var (id, _, _) = await h.CreateAsync(key: key);
        h.Clock.Now = At("2027-03-25T12:00:00-05:00");
        var repeated = false;
        // Before the run's first insert, the admin's create is retried with the same key: it makes the 3 due ones.
        h.FailEventWrite = (write, _) =>
        {
            if (write != "add" || repeated) return false;
            repeated = true;
            var again = h.SeriesApi.Create(Request(Body(), key: key), default).GetAwaiter().GetResult();
            Assert.AreEqual(3, ApiResults.Value<MW_GC.EventManager.Shared.Requests.SeriesChange>(again).Created);
            return false;
        };

        var run = await Extender(h).ExtendAllAsync(h.Clock, default);

        Assert.IsTrue(repeated);
        Assert.AreEqual(0, run.Created);
        var dates = Dates(await h.OccurrencesAsync(id));
        Assert.HasCount(11, dates);
        Assert.HasCount(11, dates.Distinct().ToArray());
    }

    [TestMethod]
    public async Task An_Occurrence_cancelled_while_the_run_reads_the_Series_is_not_made_again()
    {
        var (id, _, _) = await h.CreateAsync();
        var third = (await h.OccurrencesAsync(id))[2];
        h.Clock.Now = At("2027-03-05T12:00:00-05:00");
        var cancelled = false;
        // The admin cancels the third Occurrence while the run reads this Series: the cancel records the key on
        // the Series, then deletes the row.
        h.BeforeEventQuery = async () =>
        {
            cancelled = true;
            Assert.AreEqual(StatusCodes.Status204NoContent, ApiResults.Status(await h.EventApi.Delete(Bare(), third.Id, default)));
        };

        await Extender(h).ExtendAllAsync(h.Clock, default);

        Assert.IsTrue(cancelled);
        CollectionAssert.Contains((await h.SeriesAsync(id))!.Skipped, third.OccurrenceKey!.Value);
        CollectionAssert.DoesNotContain(Dates(await h.OccurrencesAsync(id)), Local(third.Date));
    }
}
