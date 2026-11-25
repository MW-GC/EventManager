using System.Globalization;
using Microsoft.AspNetCore.Http;
using MW_GC.EventManager.Shared.Entities;
using static MW_GC.EventManager.Api.Tests.Series.SeriesHarness;
using static MW_GC.EventManager.Api.Tests.Series.SeriesTestSupport;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// The split day across a daylight saving change (#204): an <c>all</c> edit of a split Series is refused when it
/// would make an Occurrence on the day the following Series starts in the calendar of the offset the split
/// Series was stored at, or of the offset the following Series is at, even when the edit is written at another
/// offset where that instant is still the day before. The same wall time an hour earlier is accepted.
/// </summary>
[TestClass]
public sealed class SeriesSplitDayOffsetTests
{
    private const string All = "?scope=all";
    private const string SplitDayMessage = "This Series was split by a this and following edit, so it cannot make an Occurrence on or after the day the Series that follows it starts. Send an earlier end, or edit the Series that follows it.";

    private static readonly TimeSpan Summer = TimeSpan.FromHours(-4), Winter = TimeSpan.FromHours(-5);

    private readonly SeriesHarness h = new();

    private static string Following(Guid occurrence) => $"?scope=following&occurrence={occurrence:D}";

    private static string Wall(DateTimeOffset instant, TimeSpan offset) =>
        instant.ToOffset(offset).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    // Autumn: weekly from Thursday 28 October 2027 19:00 at -04:00 (summer time), split at the fourth Occurrence
    // (18 November 19:00 -04:00) into a Series at -05:00 (winter time) that starts 18 November 19:00 -05:00.
    private async Task<Guid> SplitInAutumnAsync()
    {
        h.Clock.Now = At("2027-10-28T12:00:00-04:00");
        var (id, _, _) = await h.CreateAsync(Body(first: "2027-10-28T19:00:00-04:00"));
        var fourth = (await h.OccurrencesAsync(id))[3];
        await h.EditAsync(id, Body(name: "Winter night", first: "2027-11-18T19:00:00-05:00"), Following(fourth.Id));
        return id;
    }

    // Spring: weekly from Thursday 4 March 2027 19:00 at -05:00, split at the fourth Occurrence (25 March
    // 19:00 -05:00) into a Series written at -04:00 that starts 25 March 22:00 -04:00.
    private async Task<Guid> SplitInSpringAsync()
    {
        var (id, _, _) = await h.CreateAsync();
        var fourth = (await h.OccurrencesAsync(id))[3];
        await h.EditAsync(id, Body(name: "Summer night", first: "2027-03-25T22:00:00-04:00"), Following(fourth.Id));
        return id;
    }

    private async Task<string> StoredEndAsync(Guid id) => Wall((await h.SeriesAsync(id))!.Schedule.End!.Value, Summer);

    // The days, in the calendar of the offset given, that hold more than one Event.
    private async Task<string[]> DoubledDays(TimeSpan offset) =>
        (await h.EventsAsync()).GroupBy(e => Wall(e.Date, offset)[..10]).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();

    private async Task AssertRefusedAsync(Guid id, object body)
    {
        var before = (await h.EventsAsync()).Select(e => e.Date).ToList();
        var stored = (await h.SeriesAsync(id))!;

        var result = await h.SeriesApi.Update(Request(body, query: All), id, default);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(result));
        Assert.AreEqual(SplitDayMessage, ApiResults.Message(result));
        var after = (await h.SeriesAsync(id))!;
        Assert.AreEqual(stored.Name, after.Name);
        Assert.AreEqual(stored.Schedule, after.Schedule);
        CollectionAssert.AreEqual(before, (await h.EventsAsync()).Select(e => e.Date).ToList());
    }

    [TestMethod]
    public async Task An_all_edit_at_winter_time_cannot_reach_the_split_day_at_the_summer_time_the_split_Series_was_stored_at()
    {
        var old = await SplitInAutumnAsync();

        // Wednesday 27 October 23:30 -05:00 is Thursday 28 October 00:30 -04:00: weekly, it reaches Thursday
        // 18 November 00:30 -04:00, the split day in the calendar the old Series was stored at, though it is still
        // Wednesday 17 November at -05:00, the offset of both the edit and the following Series.
        await AssertRefusedAsync(old, Body(name: "Late Wednesday", first: "2027-10-27T23:30:00-05:00", end: await StoredEndAsync(old)));
    }

    [TestMethod]
    public async Task An_all_edit_at_winter_time_an_hour_earlier_stays_off_the_split_day_at_summer_time()
    {
        var old = await SplitInAutumnAsync();

        // Wednesday 27 October 22:30 -05:00 is Wednesday 23:30 -04:00: the day before the split day in both calendars.
        var change = await h.EditAsync(old, Body(name: "Late Wednesday", first: "2027-10-27T22:30:00-05:00", end: await StoredEndAsync(old)), All);

        Assert.AreEqual("Late Wednesday", change.Series!.Name);
        Assert.AreEqual("2027-11-17T23:30:00-04:00", Wall((await h.OccurrencesAsync(old))[^1].Date, Summer));
        Assert.IsEmpty(await DoubledDays(Summer));
        Assert.IsEmpty(await DoubledDays(Winter));
    }

    [TestMethod]
    public async Task An_all_edit_cannot_reach_the_split_day_at_the_summer_time_the_following_Series_is_written_at()
    {
        var old = await SplitInSpringAsync();

        // Wednesday 3 March 23:30 -05:00 is Thursday 4 March 00:30 -04:00: weekly, it reaches Thursday 25 March
        // 00:30 -04:00, the day the following Series starts in its own calendar, though it is still Wednesday
        // 24 March at -05:00, the offset of both the edit and the split Series.
        await AssertRefusedAsync(old, Body(name: "Late Wednesday", first: "2027-03-03T23:30:00-05:00", end: "2027-03-25T18:59:59-05:00"));
    }

    [TestMethod]
    public async Task An_all_edit_an_hour_earlier_stays_off_the_split_day_at_the_summer_time_of_the_following_Series()
    {
        var old = await SplitInSpringAsync();

        // Wednesday 3 March 22:30 -05:00 is Wednesday 23:30 -04:00: the day before the split day in both calendars.
        var change = await h.EditAsync(old, Body(name: "Late Wednesday", first: "2027-03-03T22:30:00-05:00", end: "2027-03-25T18:59:59-05:00"), All);

        Assert.AreEqual("Late Wednesday", change.Series!.Name);
        Assert.AreEqual("2027-03-24T23:30:00-04:00", Wall((await h.OccurrencesAsync(old))[^1].Date, Summer));
        Assert.IsEmpty(await DoubledDays(Summer));
        Assert.IsEmpty(await DoubledDays(Winter));
    }
}
