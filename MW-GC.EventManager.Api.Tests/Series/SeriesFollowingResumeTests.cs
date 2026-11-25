using Microsoft.AspNetCore.Http;
using static MW_GC.EventManager.Api.Tests.Series.SeriesHarness;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// A "this and following" edit that stopped part-way (#204): the process died after the chosen Occurrence was
/// deleted. Repeating the same request finishes the job, and never touches a Modified Occurrence or brings back
/// a cancelled one.
/// </summary>
[TestClass]
public sealed class SeriesFollowingResumeTests
{
    private readonly SeriesHarness h = new() { ListEventsByDate = true };

    private static readonly object LateNight = Body(name: "Late night", first: "2027-03-25T21:00:00-05:00");

    private static string Following(Guid occurrence) => $"?scope=following&occurrence={occurrence:D}";

    // Runs the edit from the fourth Occurrence (25 March) and stops it at the given Event delete, as a process
    // that died there would. The Occurrences are deleted oldest first, so the chosen one goes first.
    private async Task StopAtDeleteAsync(Guid seriesId, Guid chosen, int stopAt)
    {
        var deletes = 0;
        h.FailEventWrite = (write, _) => write == "delete" && ++deletes == stopAt;
        var stopped = await h.SeriesApi.Update(Request(LateNight, query: Following(chosen)), seriesId, default);
        h.FailEventWrite = null;
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, ApiResults.Status(stopped));
        Assert.IsFalse(h.Events.ContainsKey(chosen.ToString("D")), "the chosen Occurrence is gone");
    }

    [TestMethod]
    public async Task A_repeat_that_names_the_version_the_first_request_read_is_a_409_and_changes_nothing()
    {
        var (id, _, original) = await h.CreateAsync();
        var chosen = (await h.OccurrencesAsync(id))[3];
        await StopAtDeleteAsync(id, chosen.Id, stopAt: 3);
        var before = Dates(await h.EventsAsync());

        var again = await h.SeriesApi.Update(Request(LateNight, original, Following(chosen.Id)), id, default);

        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(again));
        CollectionAssert.AreEqual(before, Dates(await h.EventsAsync()));
    }

    private async Task<string[]> DoubledDays() =>
        (await h.EventsAsync()).GroupBy(e => Local(e.Date)[..10]).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();

    [TestMethod]
    public async Task A_following_edit_stopped_after_the_chosen_Occurrence_went_finishes_when_repeated()
    {
        var (id, _, _) = await h.CreateAsync(key: Guid.Parse("11111111-2222-3333-4444-555555555555"));
        var chosen = (await h.OccurrencesAsync(id))[3];
        await StopAtDeleteAsync(id, chosen.Id, stopAt: 3);

        var change = await h.EditAsync(id, LateNight, Following(chosen.Id));

        // The same derived id the uninterrupted edit gives (pinned in SeriesEditFollowingTests).
        Assert.AreEqual(Guid.Parse("ba596c9b-cb9b-8f6e-4bad-8796702ba904"), change.NewSeries!.Id);
        Assert.HasCount(2, h.SeriesRows);
        Assert.AreEqual("2027-03-25T18:59:59-05:00", Local((await h.SeriesAsync(id))!.Schedule.End!.Value));
        CollectionAssert.AreEqual(
            new[] { "2027-03-04T19:00:00-05:00", "2027-03-11T19:00:00-05:00", "2027-03-18T19:00:00-05:00" },
            Dates(await h.OccurrencesAsync(id)));
        var moved = await h.OccurrencesAsync(change.NewSeries.Id);
        CollectionAssert.AreEqual(
            new[]
            {
                "2027-03-25T21:00:00-05:00", "2027-04-01T21:00:00-05:00", "2027-04-08T21:00:00-05:00",
                "2027-04-15T21:00:00-05:00", "2027-04-22T21:00:00-05:00",
            },
            Dates(moved));
        Assert.IsTrue(moved.All(o => o.Name == "Late night" && !o.Modified));
        Assert.HasCount(8, h.Events);
    }

    [TestMethod]
    public async Task Finishing_a_stopped_following_edit_keeps_a_Modified_leftover_and_never_brings_back_a_cancelled_one()
    {
        var (id, _, _) = await h.CreateAsync();
        var occurrences = await h.OccurrencesAsync(id);
        await StopAtDeleteAsync(id, occurrences[3].Id, stopAt: 2);
        // Before the repeat, 8 April is edited by hand and 15 April is cancelled; both are still the old Series'.
        await h.EditOneAsync(occurrences[5], "Special");
        Assert.AreEqual(StatusCodes.Status204NoContent, ApiResults.Status(await h.EventApi.Delete(Bare(), occurrences[6].Id, default)));

        var change = await h.EditAsync(id, LateNight, Following(occurrences[3].Id));

        var special = (await h.EventsAsync()).Single(e => e.Id == occurrences[5].Id);
        Assert.AreEqual("Special", special.Name);
        Assert.AreEqual(id, special.SeriesId);
        Assert.IsTrue(special.Modified);
        Assert.AreEqual("2027-04-08T19:00:00-05:00", Local(special.Date));
        CollectionAssert.AreEqual(
            new[] { "2027-03-25T21:00:00-05:00", "2027-04-01T21:00:00-05:00", "2027-04-22T21:00:00-05:00" },
            Dates(await h.OccurrencesAsync(change.NewSeries!.Id)));
        Assert.IsFalse(Dates(await h.EventsAsync()).Any(d => d.StartsWith("2027-04-15", StringComparison.Ordinal)), "the cancelled date stays cancelled");
        var doubled = await DoubledDays();
        Assert.IsEmpty(doubled, string.Join(", ", doubled));
    }
}
