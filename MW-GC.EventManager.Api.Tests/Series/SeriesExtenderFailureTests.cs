using System.Linq.Expressions;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Moq;
using MW_GC.EventManager.API.Services;
using static MW_GC.EventManager.Api.Tests.Series.SeriesExtenderSupport;
using static MW_GC.EventManager.Api.Tests.Series.SeriesHarness;
using static MW_GC.EventManager.Api.Tests.Series.SeriesTestSupport;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// One Series that cannot be extended (#204) never stops the others: the run tries every Series, logs each
/// failure at error level with the Series' id, and reports them in its result and its one summary line.
/// </summary>
[TestClass]
public sealed class SeriesExtenderFailureTests
{
    private readonly SeriesHarness h = new();
    private readonly ListLogger<SeriesExtender> log = new();

    /// <summary>
    /// Two weekly Series; the run lists <c>broken</c> first. The clock is then 8 days on: <c>broken</c> (Thursdays) is due
    /// 29 April and 6 May, <c>healthy</c> (Fridays) only 30 April.
    /// </summary>
    private async Task<(Guid Broken, Guid Healthy)> TwoSeriesAsync()
    {
        var (broken, _, _) = await h.CreateAsync(Body(name: "Broken night"));
        var (healthy, _, _) = await h.CreateAsync(Body(name: "Healthy night", first: "2027-03-05T19:00:00-05:00"));
        ListSeriesInOrder(h, () => new[] { broken, healthy }.Select(id => h.SeriesRows[id.ToString("D")]));
        h.Clock.Now = At("2027-03-12T12:00:00-05:00");
        return (broken, healthy);
    }

    private async Task AssertHealthyExtendedAsync(Guid healthy) =>
        Assert.AreEqual("2027-04-30T19:00:00-05:00", Dates(await h.OccurrencesAsync(healthy)).Last());

    private void AssertReported(SeriesExtension run, string failedKey, int created)
    {
        CollectionAssert.AreEqual(new[] { failedKey }, run.Failed.ToArray());
        Assert.AreEqual(2, run.Seen);
        Assert.AreEqual(created, run.Created);
        var failure = log.Entries.Single(e => e.Level == LogLevel.Error && e.Exception is not null);
        StringAssert.Contains(failure.Message, failedKey);
        var summary = log.Entries.Last();
        Assert.AreEqual(LogLevel.Error, summary.Level);
        Assert.IsNull(summary.Exception);
        StringAssert.Contains(summary.Message, $"2 Series seen, {created} Occurrences created, 1 Series failed");
        StringAssert.Contains(summary.Message, failedKey);
    }

    [TestMethod]
    public async Task A_Series_whose_read_fails_is_reported_and_the_next_one_is_still_extended()
    {
        var (broken, healthy) = await TwoSeriesAsync();
        h.SeriesTable.Setup(t => t.GetEntityAsync<TableEntity>(It.IsAny<string>(), broken.ToString("D"), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Busy());

        var run = await Extender(h, log).ExtendAllAsync(h.Clock, default);

        AssertReported(run, broken.ToString("D"), created: 1);
        await AssertHealthyExtendedAsync(healthy);
        Assert.HasCount(8, await h.OccurrencesAsync(broken));
    }

    [TestMethod]
    public async Task A_Series_whose_Occurrences_cannot_be_read_is_reported_and_the_next_one_is_still_extended()
    {
        var (broken, healthy) = await TwoSeriesAsync();
        // The first read of the Event table (the first Series' Occurrences) fails; the next one works.
        h.BeforeEventQuery = () => throw Busy();

        var run = await Extender(h, log).ExtendAllAsync(h.Clock, default);

        AssertReported(run, broken.ToString("D"), created: 1);
        await AssertHealthyExtendedAsync(healthy);
    }

    [TestMethod]
    public async Task A_Series_whose_Occurrence_cannot_be_written_is_reported_the_next_one_is_still_extended_and_a_later_run_finishes_it()
    {
        var (broken, healthy) = await TwoSeriesAsync();
        var due = SeriesOccurrences.OccurrenceId(broken, At("2027-04-29T19:00:00-05:00")).ToString("D");
        h.FailEventWrite = (write, rowKey) => write == "add" && rowKey == due;

        var run = await Extender(h, log).ExtendAllAsync(h.Clock, default);

        AssertReported(run, broken.ToString("D"), created: 1);
        await AssertHealthyExtendedAsync(healthy);
        Assert.AreEqual("2027-04-22T19:00:00-05:00", Dates(await h.OccurrencesAsync(broken)).Last());

        h.FailEventWrite = null;
        var next = await Extender(h).ExtendAllAsync(h.Clock, default);
        Assert.AreEqual(2, next.Created);
        Assert.IsEmpty(next.Failed);
        Assert.AreEqual("2027-05-06T19:00:00-05:00", Dates(await h.OccurrencesAsync(broken)).Last());
    }

    [TestMethod]
    [DataRow("{\"first\":", DisplayName = "a schedule that is not JSON")]
    [DataRow("{\"first\":\"2027-03-04T19:00:00-05:00\",\"rule\":{\"kind\":\"MonthlyNthWeekday\"}}", DisplayName = "a rule the generator refuses")]
    public async Task A_malformed_Series_row_is_reported_and_the_next_one_is_still_extended(string schedule)
    {
        var (broken, healthy) = await TwoSeriesAsync();
        h.SeriesRows[broken.ToString("D")]["Schedule"] = schedule;

        var run = await Extender(h, log).ExtendAllAsync(h.Clock, default);

        AssertReported(run, broken.ToString("D"), created: 1);
        await AssertHealthyExtendedAsync(healthy);
    }

    [TestMethod]
    public async Task A_Series_row_whose_key_is_not_an_id_is_reported_and_the_next_one_is_still_extended()
    {
        var (_, healthy) = await TwoSeriesAsync();
        var stray = new TableEntity(TableNames.SeriesPartition, "not-an-id") { ["Name"] = "Stray" };
        ListSeriesInOrder(h, () => [stray, h.SeriesRows[healthy.ToString("D")]]);

        var run = await Extender(h, log).ExtendAllAsync(h.Clock, default);

        AssertReported(run, "not-an-id", created: 1);
        await AssertHealthyExtendedAsync(healthy);
    }

    [TestMethod]
    public async Task A_row_in_another_partition_of_the_Series_table_is_not_a_Series()
    {
        await h.CreateAsync();
        var foreign = new TableEntity("Marker", "not-a-series") { ["Name"] = "marker" };
        // The Series table query answers as storage does: only the rows the filter matches.
        h.SeriesTable.Setup(t => t.QueryAsync(It.IsAny<Expression<Func<TableEntity, bool>>>(), It.IsAny<int?>(),
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns((Expression<Func<TableEntity, bool>> filter, int? _, IEnumerable<string> _, CancellationToken _) =>
                AsyncPageable<TableEntity>.FromPages([Page<TableEntity>.FromValues(
                    h.SeriesRows.Values.Append(foreign).Where(filter.Compile()).ToList(), null, Mock.Of<Response>())]));
        h.Clock.Now = At("2027-03-12T12:00:00-05:00");

        var run = await Extender(h, log).ExtendAllAsync(h.Clock, default);

        Assert.AreEqual(1, run.Seen);
        Assert.IsEmpty(run.Failed);
        Assert.AreEqual(2, run.Created);
    }

    [TestMethod]
    public async Task A_Series_deleted_after_the_run_listed_it_is_neither_extended_nor_a_failure()
    {
        var (gone, healthy) = await TwoSeriesAsync();
        h.SeriesTable.Setup(t => t.GetEntityAsync<TableEntity>(It.IsAny<string>(), gone.ToString("D"), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Azure.RequestFailedException(404, "The specified resource does not exist.", "ResourceNotFound", null));

        var run = await Extender(h, log).ExtendAllAsync(h.Clock, default);

        Assert.IsEmpty(run.Failed);
        Assert.AreEqual(1, run.Created);
        Assert.HasCount(8, await h.OccurrencesAsync(gone));
        await AssertHealthyExtendedAsync(healthy);
    }

    [TestMethod]
    public async Task With_no_Series_at_all_the_run_writes_nothing_and_logs_one_line()
    {
        var run = await Extender(h, log).ExtendAllAsync(h.Clock, default);

        Assert.AreEqual(0, run.Seen);
        Assert.AreEqual(0, run.Created);
        Assert.IsEmpty(run.Failed);
        Assert.AreEqual(0, Writes(h.EventTable));
        var line = log.Entries.Single();
        Assert.AreEqual(LogLevel.Information, line.Level);
        StringAssert.Contains(line.Message, "0 Series seen, 0 Occurrences created, 0 Series failed");
    }

    [TestMethod]
    public async Task A_run_with_no_failure_logs_one_information_line_with_its_totals()
    {
        await TwoSeriesAsync();

        var run = await Extender(h, log).ExtendAllAsync(h.Clock, default);

        Assert.AreEqual(3, run.Created);
        var line = log.Entries.Single();
        Assert.AreEqual(LogLevel.Information, line.Level);
        StringAssert.Contains(line.Message, "2 Series seen, 3 Occurrences created, 0 Series failed");
    }

    [TestMethod]
    public async Task A_cancelled_run_stops_and_reports_no_failure()
    {
        await TwoSeriesAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => Extender(h, log).ExtendAllAsync(h.Clock, cancelled.Token));

        Assert.IsFalse(log.Entries.Any(e => e.Level == LogLevel.Error), string.Join(" | ", log.Entries.Select(e => e.Message)));
    }

    [TestMethod]
    public async Task A_run_cancelled_while_it_reads_a_Series_stops_there_and_reports_no_failure()
    {
        var (_, healthy) = await TwoSeriesAsync();
        using var cancelled = new CancellationTokenSource();
        // The host stops while the first Series' Occurrences are read: storage answers with the cancellation.
        h.BeforeEventQuery = () =>
        {
            cancelled.Cancel();
            throw new OperationCanceledException(cancelled.Token);
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => Extender(h, log).ExtendAllAsync(h.Clock, cancelled.Token));

        Assert.IsFalse(log.Entries.Any(e => e.Level == LogLevel.Error), string.Join(" | ", log.Entries.Select(e => e.Message)));
        Assert.HasCount(8, await h.OccurrencesAsync(healthy));
    }
}
