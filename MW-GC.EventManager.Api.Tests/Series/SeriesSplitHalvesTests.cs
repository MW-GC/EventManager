using Microsoft.AspNetCore.Http;
using static MW_GC.EventManager.Api.Tests.Series.SeriesHarness;
using static MW_GC.EventManager.Api.Tests.Series.SeriesTestSupport;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// An <c>all</c> edit of either half of a split Series (#204): the Series a "this and following" edit started
/// never moves its start back before the key it was split from, and the Series it was split from never makes
/// an Occurrence on or after the day the one that follows it starts, so the two never share a day. Ordinary
/// edits of either half still work.
/// </summary>
[TestClass]
public sealed class SeriesSplitHalvesTests
{
    private const string All = "?scope=all";
    private const string StoredEnd = "2027-03-25T18:59:59-05:00";
    private const string SplitEndMessage = "This Series was split by a this and following edit, so its end cannot be removed or moved later. Send its stored end, or edit the Series that follows it.";
    private const string SplitDayMessage = "This Series was split by a this and following edit, so it cannot make an Occurrence on or after the day the Series that follows it starts. Send an earlier end, or edit the Series that follows it.";
    private const string SplitStartMessage = "This Series was started by a this and following edit, so its start cannot move before the Occurrence it was split at (or its stored start, if that is earlier). Edit the Series before it instead.";

    private readonly SeriesHarness h = new();

    private static string Following(Guid occurrence) => $"?scope=following&occurrence={occurrence:D}";

    // Weekly from Thursday 4 March 19:00, split at the fourth Occurrence (25 March 19:00) into a Series that
    // starts at newFirst (by default 25 March 21:00).
    private async Task<(Guid Old, Guid Next)> SplitAsync(string newFirst = "2027-03-25T21:00:00-05:00")
    {
        var (id, _, _) = await h.CreateAsync();
        var fourth = (await h.OccurrencesAsync(id))[3];
        var change = await h.EditAsync(id, Body(name: "Late night", first: newFirst), Following(fourth.Id));
        return (id, change.NewSeries!.Id);
    }

    // The days (at -05:00) that hold more than one Event.
    private async Task<string[]> DoubledDays() =>
        (await h.EventsAsync()).GroupBy(e => Local(e.Date)[..10]).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();

    private async Task AssertRefusedAsync(Guid id, object body, string message)
    {
        var before = Dates(await h.EventsAsync());
        var series = (await h.SeriesAsync(id))!;

        var result = await h.SeriesApi.Update(Request(body, query: All), id, default);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(result));
        Assert.AreEqual(message, ApiResults.Message(result));
        var after = (await h.SeriesAsync(id))!;
        Assert.AreEqual(series.Name, after.Name);
        Assert.AreEqual(series.Schedule, after.Schedule);
        CollectionAssert.AreEqual(before, Dates(await h.EventsAsync()));
        var doubled = await DoubledDays();
        Assert.IsEmpty(doubled, string.Join(", ", doubled));
    }

    [TestMethod]
    public async Task An_all_edit_cannot_move_the_start_of_the_Series_a_split_started_back_into_the_one_before_it()
    {
        var (_, next) = await SplitAsync();

        await AssertRefusedAsync(next, Body(name: "Late night", first: "2027-03-04T21:00:00-05:00"), SplitStartMessage);
        Assert.AreEqual(At("2027-03-25T19:00:00-05:00"), (await h.SeriesAsync(next))!.SplitFrom);
    }

    [TestMethod]
    [DataRow("2027-04-01T22:00:00-05:00", new[] { "2027-04-01T22:00:00-05:00", "2027-04-08T22:00:00-05:00", "2027-04-15T22:00:00-05:00", "2027-04-22T22:00:00-05:00" })]
    [DataRow("2027-03-25T20:00:00-05:00", new[] { "2027-03-25T20:00:00-05:00", "2027-04-01T20:00:00-05:00", "2027-04-08T20:00:00-05:00", "2027-04-15T20:00:00-05:00", "2027-04-22T20:00:00-05:00" })]
    public async Task An_all_edit_of_the_Series_a_split_started_may_start_later_or_at_another_time_after_the_split(string first, string[] expected)
    {
        var (old, next) = await SplitAsync();

        await h.EditAsync(next, Body(name: "Later night", first: first), All);

        CollectionAssert.AreEqual(expected, Dates(await h.OccurrencesAsync(next)));
        Assert.HasCount(3, await h.OccurrencesAsync(old));
        Assert.IsEmpty(await DoubledDays());
    }

    [TestMethod]
    public async Task A_Series_a_split_started_before_the_chosen_Occurrence_keeps_that_start_through_an_all_edit()
    {
        var (_, next) = await SplitAsync(newFirst: "2027-03-20T21:00:00-05:00");

        var change = await h.EditAsync(next, Body(name: "Saturday night", first: "2027-03-20T21:00:00-05:00"), All);

        Assert.AreEqual("Saturday night", change.Series!.Name);
        Assert.AreEqual("2027-03-20T21:00:00-05:00", Dates(await h.OccurrencesAsync(next))[0]);
        Assert.IsEmpty(await DoubledDays());
    }

    [TestMethod]
    public async Task The_split_start_survives_an_all_edit_of_the_Series_a_split_started()
    {
        var (_, next) = await SplitAsync();
        await h.EditAsync(next, Body(name: "Renamed late night", first: "2027-03-25T21:00:00-05:00"), All);

        await AssertRefusedAsync(next, Body(name: "Renamed late night", first: "2027-03-04T21:00:00-05:00"), SplitStartMessage);
    }

    [TestMethod]
    public async Task An_all_edit_of_the_split_Series_cannot_move_its_time_onto_the_day_the_next_one_starts()
    {
        var (old, _) = await SplitAsync();

        // The stored end is kept, but 18:00 on 25 March is before it: the old Series would make it beside 21:00.
        await AssertRefusedAsync(old, Body(first: "2027-03-04T18:00:00-05:00", end: StoredEnd), SplitDayMessage);
    }

    [TestMethod]
    public async Task An_all_edit_of_the_split_Series_cannot_reach_the_day_the_next_one_starts_by_changing_its_offset()
    {
        var (old, _) = await SplitAsync();

        // 23:30 at +00:00 is 18:30 at -05:00: before the stored end and before 26 March in UTC, but on 25 March
        // in the calendar of both Series as they were stored.
        await AssertRefusedAsync(old, Body(first: "2027-03-04T23:30:00+00:00", end: StoredEnd), SplitDayMessage);
    }

    [TestMethod]
    public async Task An_all_edit_of_the_split_Series_cannot_reach_the_day_the_next_one_starts_when_it_starts_before_the_split()
    {
        // The new Series starts on Saturday 20 March, before the chosen Thursday; a Saturday rule for the old one
        // would make 20 March 19:00 beside it, although the old Series' end is still 25 March.
        var (old, _) = await SplitAsync(newFirst: "2027-03-20T21:00:00-05:00");

        await AssertRefusedAsync(old, Body(first: "2027-03-06T19:00:00-05:00", end: StoredEnd), SplitDayMessage);
    }

    [TestMethod]
    public async Task An_all_edit_of_the_split_Series_with_an_earlier_end_may_move_its_time_earlier()
    {
        var (old, next) = await SplitAsync();

        await h.EditAsync(old, Body(name: "Early night", first: "2027-03-04T18:00:00-05:00", end: "2027-03-20T00:00:00-05:00"), All);

        CollectionAssert.AreEqual(new[] { "2027-03-04T18:00:00-05:00", "2027-03-11T18:00:00-05:00", "2027-03-18T18:00:00-05:00" },
            Dates(await h.OccurrencesAsync(old)));
        Assert.HasCount(5, await h.OccurrencesAsync(next));
        Assert.IsEmpty(await DoubledDays());
    }

    [TestMethod]
    public async Task Once_the_split_day_is_past_an_all_edit_of_the_split_Series_may_move_its_time_earlier()
    {
        var (old, _) = await SplitAsync();
        h.Clock.Now = At("2027-03-26T12:00:00-05:00");

        var change = await h.EditAsync(old, Body(name: "Early night", first: "2027-03-04T18:00:00-05:00", end: StoredEnd), All);

        // Every Occurrence of the old Series is past, so nothing is made or moved; only the row changes.
        Assert.AreEqual(0, change.Created + change.Updated + change.Deleted);
        Assert.AreEqual("Early night", (await h.SeriesAsync(old))!.Name);
        Assert.IsEmpty(await DoubledDays());
    }

    [TestMethod]
    public async Task An_end_exactly_at_the_split_Occurrence_is_refused_as_a_later_end()
    {
        var (old, _) = await SplitAsync();

        await AssertRefusedAsync(old, Body(end: "2027-03-25T19:00:00-05:00"), SplitEndMessage);
    }

    [TestMethod]
    public async Task The_split_mark_survives_an_all_edit_and_a_second_one_still_refuses_a_later_end()
    {
        var (old, _) = await SplitAsync();
        await h.EditAsync(old, Body(name: "Early night", end: StoredEnd), All);
        Assert.AreEqual(At("2027-03-25T19:00:00-05:00"), (await h.SeriesAsync(old))!.SplitAt);

        await AssertRefusedAsync(old, Body(name: "Early night", end: "2027-12-31T00:00:00-05:00"), SplitEndMessage);
    }
}
