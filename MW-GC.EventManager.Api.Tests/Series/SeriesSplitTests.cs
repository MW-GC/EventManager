using Microsoft.AspNetCore.Http;
using static MW_GC.EventManager.Api.Tests.Series.SeriesHarness;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// A Series a "this and following" edit split (#204): it never makes Occurrences again on the dates of the
/// Series that follows it, so no edit removes its end, moves the end later or splits it a second time, and no
/// edit removes the end of any Series that has one.
/// </summary>
[TestClass]
public sealed class SeriesSplitTests
{
    private const string All = "?scope=all";
    private const string SplitEndMessage = "This Series was split by a this and following edit, so its end cannot be removed or moved later. Send its stored end, or edit the Series that follows it.";

    private readonly SeriesHarness h = new();

    private static string Following(Guid occurrence) => $"?scope=following&occurrence={occurrence:D}";

    // Weekly from Thursday 4 March 19:00, split at the fourth Occurrence (25 March) into a 21:00 Series.
    private async Task<(Guid Old, Guid Next)> SplitAsync()
    {
        var (id, _, _) = await h.CreateAsync();
        var fourth = (await h.OccurrencesAsync(id))[3];
        var change = await h.EditAsync(id, Body(name: "Late night", first: "2027-03-25T21:00:00-05:00"), Following(fourth.Id));
        return (id, change.NewSeries!.Id);
    }

    // The days (at -05:00) that hold more than one Event.
    private async Task<string[]> DoubledDays() =>
        (await h.EventsAsync()).GroupBy(e => Local(e.Date)[..10]).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();

    [TestMethod]
    [DataRow(null)]
    [DataRow("2027-12-31T00:00:00-05:00")]
    public async Task An_all_edit_cannot_reopen_a_split_Series_into_the_dates_of_the_one_that_follows(string? end)
    {
        var (old, _) = await SplitAsync();
        var before = Dates(await h.EventsAsync());

        var result = await h.SeriesApi.Update(Request(Body(end: end), query: All), old, default);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(result));
        Assert.AreEqual(SplitEndMessage, ApiResults.Message(result));
        Assert.AreEqual("2027-03-25T18:59:59-05:00", Local((await h.SeriesAsync(old))!.Schedule.End!.Value));
        CollectionAssert.AreEqual(before, Dates(await h.EventsAsync()));
        var doubled = await DoubledDays();
        Assert.IsEmpty(doubled, string.Join(", ", doubled));
    }

    [TestMethod]
    public async Task An_all_edit_of_a_split_Series_that_sends_its_stored_end_still_works()
    {
        var (old, next) = await SplitAsync();

        var change = await h.EditAsync(old, Body(name: "Early night", end: "2027-03-25T18:59:59-05:00"), All);

        Assert.AreEqual(3, change.Updated);
        Assert.AreEqual(0, change.Created);
        var kept = await h.OccurrencesAsync(old);
        CollectionAssert.AreEqual(new[] { "2027-03-04T19:00:00-05:00", "2027-03-11T19:00:00-05:00", "2027-03-18T19:00:00-05:00" }, Dates(kept));
        Assert.IsTrue(kept.All(o => o.Name == "Early night"), string.Join(", ", kept.Select(o => o.Name)));
        Assert.HasCount(5, await h.OccurrencesAsync(next));
        Assert.IsEmpty(await DoubledDays());
    }

    [TestMethod]
    public async Task An_all_edit_that_drops_the_end_of_an_ended_Series_is_refused_and_changes_nothing()
    {
        var (id, _, _) = await h.CreateAsync(Body(end: "2027-04-01T19:00:00-05:00"));

        var result = await h.SeriesApi.Update(Request(Body(name: "Game night"), query: All), id, default);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(result));
        Assert.AreEqual("This Series has an end. Send its stored end to keep it, or a new one: an edit cannot remove it.", ApiResults.Message(result));
        var series = (await h.SeriesAsync(id))!;
        Assert.AreEqual("Thursday night", series.Name);
        Assert.AreEqual("2027-04-01T19:00:00-05:00", Local(series.Schedule.End!.Value));
        var occurrences = await h.OccurrencesAsync(id);
        Assert.HasCount(5, occurrences);
        Assert.IsTrue(occurrences.All(o => o.Name == "Thursday night"));
    }

    [TestMethod]
    public async Task A_second_following_edit_of_a_split_Series_at_another_Occurrence_is_refused()
    {
        var (old, _) = await SplitAsync();
        var second = (await h.OccurrencesAsync(old))[1];

        var result = await h.SeriesApi.Update(
            Request(Body(name: "Other night", first: "2027-03-11T20:00:00-05:00"), query: Following(second.Id)), old, default);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(result));
        Assert.AreEqual("This Series was already split by a this and following edit at another Occurrence. Edit the Series that follows it, or this one with scope all.",
            ApiResults.Message(result));
        Assert.HasCount(2, h.SeriesRows);
        Assert.AreEqual("2027-03-25T18:59:59-05:00", Local((await h.SeriesAsync(old))!.Schedule.End!.Value));
        Assert.IsEmpty(await DoubledDays());
    }
}
