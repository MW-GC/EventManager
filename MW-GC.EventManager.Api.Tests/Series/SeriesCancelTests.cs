using Microsoft.AspNetCore.Http;
using static MW_GC.EventManager.Api.Tests.Series.SeriesHarness;
using static MW_GC.EventManager.Api.Tests.Series.SeriesTestSupport;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// <c>DELETE /api/series/{id}?scope=all</c> (#204): the Series row goes, and so does every future Occurrence of
/// it that is not played; past and played Occurrences stay as history.
/// </summary>
[TestClass]
public sealed class SeriesCancelTests
{
    private const string All = "?scope=all";
    private readonly SeriesHarness h = new();

    [TestMethod]
    public async Task Cancelling_a_Series_deletes_it_and_its_future_unplayed_Occurrences()
    {
        var (id, _, _) = await h.CreateAsync();

        Assert.AreEqual(StatusCodes.Status204NoContent, ApiResults.Status(await h.SeriesApi.Delete(Bare(query: All), id, default)));

        Assert.IsNull(await h.SeriesAsync(id));
        Assert.IsEmpty(await h.EventsAsync());
    }

    [TestMethod]
    [DataRow("winner")]
    [DataRow("comment")]
    [DataRow("metrics")]
    public async Task A_played_Occurrence_stays_when_its_Series_is_cancelled(string reason)
    {
        var (id, _, _) = await h.CreateAsync();
        var third = (await h.OccurrencesAsync(id))[2];
        h.MakePlayed(third.Id, reason);

        await h.SeriesApi.Delete(Bare(query: All), id, default);

        var left = Assert.ContainsSingle(await h.EventsAsync());
        Assert.AreEqual(third.Id, left.Id);
        Assert.AreEqual(id, left.SeriesId);
    }

    [TestMethod]
    public async Task Past_Occurrences_stay_when_the_Series_is_cancelled()
    {
        var (id, _, _) = await h.CreateAsync();
        h.Clock.Now = At("2027-03-12T12:00:00-05:00");

        await h.SeriesApi.Delete(Bare(query: All), id, default);

        CollectionAssert.AreEqual(new[] { "2027-03-04T19:00:00-05:00", "2027-03-11T19:00:00-05:00" }, Dates(await h.EventsAsync()));
    }

    [TestMethod]
    public async Task A_Modified_but_unplayed_future_Occurrence_goes_with_its_Series()
    {
        var (id, _, _) = await h.CreateAsync();
        await h.EditOneAsync((await h.OccurrencesAsync(id))[2], "Special");

        await h.SeriesApi.Delete(Bare(query: All), id, default);

        Assert.IsEmpty(await h.EventsAsync());
    }

    [TestMethod]
    public async Task A_stale_If_Match_is_a_409_and_deletes_nothing()
    {
        var (id, _, original) = await h.CreateAsync();
        await h.EditAsync(id, Body(name: "Edited"), All, ifMatch: original);

        var result = await h.SeriesApi.Delete(Bare(original, All), id, default);

        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(result));
        Assert.IsNotNull(await h.SeriesAsync(id));
        Assert.HasCount(8, await h.EventsAsync());
    }

    [TestMethod]
    public async Task A_retried_cancel_after_the_row_is_gone_still_deletes_the_Occurrences_left_behind()
    {
        var (id, _, _) = await h.CreateAsync();
        h.SeriesRows.TryRemove(id.ToString("D"), out _); // as if the first cancel stopped after deleting the row

        Assert.AreEqual(StatusCodes.Status204NoContent, ApiResults.Status(await h.SeriesApi.Delete(Bare(query: All), id, default)));

        Assert.IsEmpty(await h.EventsAsync());
    }

    [TestMethod]
    public async Task Cancelling_one_Series_leaves_another_Series_and_standalone_Events_alone()
    {
        var (id, _, _) = await h.CreateAsync();
        var (other, _, _) = await h.CreateAsync(Body(name: "Friday night", first: "2027-03-05T19:00:00-05:00"));
        var solo = await h.CreateStandaloneAsync();

        await h.SeriesApi.Delete(Bare(query: All), id, default);

        Assert.HasCount(8, await h.OccurrencesAsync(other));
        Assert.IsEmpty(await h.OccurrencesAsync(id));
        var kept = (await h.EventsAsync()).Single(e => e.SeriesId is null);
        Assert.AreEqual(solo.Id, kept.Id);
        Assert.AreEqual("Solo", kept.Name);
        Assert.HasCount(1, kept.Selections);
    }
}
