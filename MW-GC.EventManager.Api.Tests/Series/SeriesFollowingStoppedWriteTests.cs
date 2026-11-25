using Azure;
using Microsoft.AspNetCore.Http;
using static MW_GC.EventManager.Api.Tests.Series.SeriesHarness;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// A "this and following" edit that stopped at a write other than an Event delete (#204): at an Event add,
/// at the old Series' update, or at the new Series' insert. Repeating the same request finishes the job: the
/// old Series keeps its earlier Occurrences and the Modified one, the new Series gets the rest, the Modified
/// row is never written, and no day holds two Events.
/// </summary>
[TestClass]
public sealed class SeriesFollowingStoppedWriteTests
{
    private readonly SeriesHarness h = new() { ListEventsByDate = true };

    private static readonly object LateNight = Body(name: "Late night", first: "2027-03-25T21:00:00-05:00");

    private static string Following(Guid occurrence) => $"?scope=following&occurrence={occurrence:D}";

    // Weekly from 4 March with 8 April edited by hand, then the edit from 25 March, stopped by the given hook
    // (a 503 at that write), then the same request again, and checks the finished state.
    private async Task FinishesWhenRepeatedAsync(Action<SeriesHarness> stop)
    {
        var (id, _, _) = await h.CreateAsync();
        var occurrences = await h.OccurrencesAsync(id);
        var special = (await h.EditOneAsync(occurrences[5], "Special")).Id;
        stop(h);
        var stopped = await h.SeriesApi.Update(Request(LateNight, query: Following(occurrences[3].Id)), id, default);
        h.FailEventWrite = null;
        h.FailSeriesWrite = null;
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, ApiResults.Status(stopped));
        var specialETag = h.Events[special.ToString("D")].ETag;

        var change = await h.EditAsync(id, LateNight, Following(occurrences[3].Id));

        CollectionAssert.AreEqual(
            new[] { "2027-03-04T19:00:00-05:00", "2027-03-11T19:00:00-05:00", "2027-03-18T19:00:00-05:00", "2027-04-08T19:00:00-05:00" },
            Dates(await h.OccurrencesAsync(id)));
        var moved = await h.OccurrencesAsync(change.NewSeries!.Id);
        CollectionAssert.AreEqual(
            new[] { "2027-03-25T21:00:00-05:00", "2027-04-01T21:00:00-05:00", "2027-04-15T21:00:00-05:00", "2027-04-22T21:00:00-05:00" },
            Dates(moved));
        Assert.IsTrue(moved.All(o => o.Name == "Late night" && !o.Modified));
        Assert.AreEqual(specialETag, h.Events[special.ToString("D")].ETag, "the Modified Occurrence is never written");
        Assert.AreEqual("Special", h.Events[special.ToString("D")].GetString("Name"));
        Assert.AreEqual("2027-03-25T18:59:59-05:00", Local((await h.SeriesAsync(id))!.Schedule.End!.Value));
        Assert.HasCount(2, h.SeriesRows);
        var doubled = (await h.EventsAsync()).GroupBy(e => Local(e.Date)[..10]).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.IsEmpty(doubled, string.Join(", ", doubled));
    }

    [TestMethod]
    public async Task A_following_edit_stopped_at_an_Event_add_finishes_when_repeated()
    {
        var adds = 0;
        // The moving Occurrences are all deleted first; the first add is the new Series' 25 March.
        await FinishesWhenRepeatedAsync(h => h.FailEventWrite = (write, _) => write == "add" && ++adds == 1);
    }

    [TestMethod]
    public async Task A_following_edit_stopped_at_the_old_Series_update_finishes_when_repeated()
    {
        await FinishesWhenRepeatedAsync(h => h.FailSeriesWrite = (write, _) => write == "update");
    }

    [TestMethod]
    public async Task A_following_edit_stopped_at_the_new_Series_insert_finishes_when_repeated()
    {
        await FinishesWhenRepeatedAsync(h => h.FailSeriesWrite = (write, _) => write == "add");
    }
}
