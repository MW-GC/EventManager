using Microsoft.AspNetCore.Http;
using static MW_GC.EventManager.Api.Tests.Series.SeriesHarness;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// Writes that land while a Series edit runs (#204): the edit reads the Occurrences, then deletes or rewrites
/// them one by one. A cancel-one or an Event metrics save that lands in between must win: the cancelled
/// Occurrence never comes back, and the newly played one is never deleted or given a twin.
/// </summary>
[TestClass]
public sealed class SeriesRaceTests
{
    private readonly SeriesHarness h = new();

    [TestMethod]
    public async Task An_Occurrence_cancelled_during_a_following_edit_does_not_come_back_under_the_new_Series()
    {
        var (id, _, _) = await h.CreateAsync();
        var occurrences = await h.OccurrencesAsync(id);
        string? cancelled = null;
        // Right before the edit deletes its first moving Occurrence, an admin cancels another one.
        h.BeforeEventDelete = async rowKey =>
        {
            var victim = occurrences.Skip(3).First(o => o.Id.ToString("D") != rowKey);
            cancelled = Local(victim.Date);
            Assert.AreEqual(StatusCodes.Status204NoContent, ApiResults.Status(await h.EventApi.Delete(Bare(), victim.Id, default)));
        };

        var change = await h.EditAsync(id, Body(name: "Late night", first: "2027-03-25T19:00:00-05:00"),
            $"?scope=following&occurrence={occurrences[3].Id:D}");

        Assert.IsNotNull(cancelled);
        var moved = await h.OccurrencesAsync(change.NewSeries!.Id);
        CollectionAssert.DoesNotContain(Dates(moved), cancelled, string.Join(", ", Dates(moved)));
        Assert.HasCount(4, moved);
        var newSeries = (await h.SeriesAsync(change.NewSeries.Id))!;
        Assert.IsTrue(newSeries.Skipped.Any(k => Local(k) == cancelled), "the new Series records the cancelled key");
        // A later run of the new Series (a repeated edit) still does not make it.
        await h.EditAsync(change.NewSeries.Id, Body(name: "Later night", first: "2027-03-25T19:00:00-05:00"), "?scope=all");
        CollectionAssert.DoesNotContain(Dates(await h.OccurrencesAsync(change.NewSeries.Id)), cancelled);
    }

    [TestMethod]
    public async Task An_Occurrence_cancelled_during_an_all_edit_does_not_come_back()
    {
        var (id, _, _) = await h.CreateAsync();
        var occurrences = await h.OccurrencesAsync(id);
        string? cancelled = null;
        h.BeforeEventDelete = async rowKey =>
        {
            var victim = occurrences.First(o => o.Id.ToString("D") != rowKey);
            cancelled = Local(victim.Date)[..10];
            await h.EventApi.Delete(Bare(), victim.Id, default);
        };

        // A new time of day: every Occurrence is deleted and made again at 20:00.
        await h.EditAsync(id, Body(first: "2027-03-04T20:00:00-05:00"), "?scope=all");

        var dates = Dates(await h.OccurrencesAsync(id));
        Assert.IsNotNull(cancelled);
        Assert.IsFalse(dates.Any(d => d.StartsWith(cancelled, StringComparison.Ordinal)), string.Join(", ", dates));
        Assert.HasCount(7, dates);
    }

    [TestMethod]
    public async Task Event_metrics_saved_during_an_edit_keep_that_Occurrence_and_give_it_no_twin()
    {
        var (id, _, _) = await h.CreateAsync();
        var occurrences = await h.OccurrencesAsync(id);
        Guid? played = null;
        h.BeforeEventDelete = rowKey =>
        {
            // Another Occurrence the edit has not reached yet gets its Event metrics saved now.
            var target = occurrences.First(o => o.Id.ToString("D") != rowKey);
            played = target.Id;
            h.SeedMetrics(target.Id, attendees: 9);
            return Task.CompletedTask;
        };

        await h.EditAsync(id, Body(first: "2027-03-04T20:00:00-05:00"), "?scope=all");

        Assert.IsNotNull(played);
        var after = await h.OccurrencesAsync(id);
        var kept = after.Single(o => o.Id == played);
        Assert.AreEqual("19:00", Local(kept.Date)[11..16]);
        Assert.AreEqual(1, after.Count(o => Local(o.Date)[..10] == Local(kept.Date)[..10]), string.Join(", ", Dates(after)));
        Assert.IsTrue(h.Metrics.ContainsKey(played.Value.ToString("D")), "the metrics row is not deleted");
        Assert.HasCount(8, after);
    }
}
