using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Series;
using static MW_GC.EventManager.Api.Tests.Series.SeriesTestSupport;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// How the avoid recent, players tonight and longest duration Filters sit in a stored Series row (#232): inside
/// the one <c>Filters</c> JSON value, by their camelCase names, left out while off, so a row stored before they
/// existed reads them as off and is written back unchanged. No migration.
/// </summary>
[TestClass]
public sealed class SeriesFilterStorageTests
{
    private readonly SeriesHarness h = new();

    // A Filters value as a row stored before #232 holds it (the shape the store writes: camelCase, compact).
    private const string OldFilters = "{\"themedOnly\":true,\"gameIds\":[],\"themeIds\":[],\"holidayIds\":[]}";

    private static SeriesEntity Series(SeriesFilters filters) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Thursday night",
        Schedule = new RecurrenceSeries(At("2027-03-04T19:00:00-05:00"), RecurrenceRule.Weekly()),
        Filters = filters,
    };

    [TestMethod]
    public async Task The_three_are_stored_by_name_inside_the_Filters_value_and_read_back()
    {
        var series = Series(new SeriesFilters { ThemedOnly = true, AvoidRecent = 3, PlayersTonight = 4, LongestDuration = 90 });
        await h.SeriesStore.UpsertAsync(series);

        var stored = h.SeriesRows[series.Id.ToString("D")].GetString("Filters");
        Assert.AreEqual(
            "{\"themedOnly\":true,\"gameIds\":[],\"themeIds\":[],\"holidayIds\":[],\"avoidRecent\":3,\"playersTonight\":4,\"longestDuration\":90}",
            stored);
        var read = (await h.SeriesStore.GetAsync(series.Id))!;
        Assert.AreEqual(3, read.Filters.AvoidRecent);
        Assert.AreEqual(4, read.Filters.PlayersTonight);
        Assert.AreEqual(90, read.Filters.LongestDuration);
    }

    [TestMethod]
    public async Task A_row_stored_before_the_three_existed_reads_them_as_off_and_is_written_back_unchanged()
    {
        var series = Series(new SeriesFilters { ThemedOnly = true });
        await h.SeriesStore.UpsertAsync(series);
        var key = series.Id.ToString("D");
        h.SeriesRows[key]["Filters"] = OldFilters;

        var read = (await h.SeriesStore.GetAsync(series.Id))!;
        Assert.IsNull(read.Filters.AvoidRecent);
        Assert.IsNull(read.Filters.PlayersTonight);
        Assert.IsNull(read.Filters.LongestDuration);
        Assert.IsTrue(read.Filters.ThemedOnly);

        await h.SeriesStore.UpsertAsync(read);

        Assert.AreEqual(OldFilters, h.SeriesRows[key].GetString("Filters"));
    }

    [TestMethod]
    public async Task One_Filter_on_writes_only_that_one()
    {
        var series = Series(new SeriesFilters { PlayersTonight = 6 });
        await h.SeriesStore.UpsertAsync(series);

        Assert.AreEqual(
            "{\"themedOnly\":false,\"gameIds\":[],\"themeIds\":[],\"holidayIds\":[],\"playersTonight\":6}",
            h.SeriesRows[series.Id.ToString("D")].GetString("Filters"));
        var read = (await h.SeriesStore.GetAsync(series.Id))!;
        Assert.IsNull(read.Filters.AvoidRecent);
        Assert.AreEqual(6, read.Filters.PlayersTonight);
        Assert.IsNull(read.Filters.LongestDuration);
    }
}
