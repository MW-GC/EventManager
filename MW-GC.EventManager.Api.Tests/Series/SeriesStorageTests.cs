using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Series;
using static MW_GC.EventManager.Api.Tests.Series.SeriesTestSupport;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// How a Series row is stored (#204): the first instant keeps its UTC offset, the rule's enums are written by
/// name, and a long list of cancelled keys is split over numbered chunk properties like an Event's Selections,
/// so no property passes Table Storage's 32,768-character limit.
/// </summary>
[TestClass]
public sealed class SeriesStorageTests
{
    private readonly SeriesHarness h = new();

    private static SeriesEntity Series(int skipped) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Thursday night",
        Schedule = new RecurrenceSeries(At("2027-03-04T19:00:00-05:00"),
            RecurrenceRule.MonthlyNthWeekday(WeekOfMonth.Last, DayOfWeek.Friday), At("2030-12-31T00:00:00-05:00")),
        SlotCount = 4,
        UniqueGamesOnly = false,
        Filters = new SeriesFilters { ThemedOnly = true, GameIds = [Guid.NewGuid()] },
        Skipped = Enumerable.Range(0, skipped).Select(i => At("2027-03-04T19:00:00-05:00").AddDays(7 * i)).ToList(),
    };

    [TestMethod]
    public async Task A_Series_reads_back_as_written_with_the_offset_of_its_first_instant()
    {
        var series = Series(skipped: 3);
        await h.SeriesStore.UpsertAsync(series);

        var read = (await h.SeriesStore.GetAsync(series.Id))!;

        Assert.AreEqual("2027-03-04T19:00:00-05:00", SeriesHarness.Local(read.Schedule.First));
        Assert.AreEqual(TimeSpan.FromHours(-5), read.Schedule.First.Offset);
        Assert.AreEqual(series.Schedule, read.Schedule);
        Assert.AreEqual(4, read.SlotCount);
        Assert.IsFalse(read.UniqueGamesOnly);
        Assert.IsTrue(read.Filters.ThemedOnly);
        CollectionAssert.AreEqual(series.Filters.GameIds, read.Filters.GameIds);
        CollectionAssert.AreEqual(series.Skipped, read.Skipped);
        StringAssert.Contains(h.SeriesRows[series.Id.ToString("D")].GetString("Schedule"),
            "\"rule\":{\"kind\":\"MonthlyNthWeekday\",\"week\":\"Last\",\"weekday\":\"Friday\",\"preset\":null}");
    }

    [TestMethod]
    public async Task A_long_list_of_cancelled_keys_is_split_into_chunks_and_joined_on_read()
    {
        // 28 characters per key ("2027-03-04T19:00:00-05:00" quoted, and a comma): 3,000 keys is about
        // 84,000 characters, three chunks of at most 30,000.
        var series = Series(skipped: 3000);
        await h.SeriesStore.UpsertAsync(series);

        var row = h.SeriesRows[series.Id.ToString("D")];
        var chunks = row.Keys.Where(k => k == "Skipped" || k.StartsWith("Skipped__", StringComparison.Ordinal)).ToList();
        Assert.IsGreaterThanOrEqualTo(3, chunks.Count, string.Join(", ", chunks));
        Assert.IsTrue(chunks.All(k => row.GetString(k).Length <= 32_768), "No chunk passes the per-property limit.");
        CollectionAssert.AreEqual(series.Skipped, (await h.SeriesStore.GetAsync(series.Id))!.Skipped);
    }

    [TestMethod]
    public async Task A_list_too_long_even_in_chunks_is_refused_before_anything_is_written()
    {
        // 28 characters per key: 40,000 keys is about 1,120,000 characters, over 2 MB of UTF-16, past the
        // 1 MiB entity limit.
        var series = Series(skipped: 40_000);

        var refused = await Assert.ThrowsExactlyAsync<StoredValueTooLargeException>(() => h.SeriesStore.UpsertAsync(series));

        Assert.AreEqual("Skipped", refused.Property);
        Assert.IsEmpty(h.SeriesRows);
    }
}
