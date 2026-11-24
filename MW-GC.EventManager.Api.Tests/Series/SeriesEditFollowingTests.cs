using Microsoft.AspNetCore.Http;
using MW_GC.EventManager.API.Functions;
using static MW_GC.EventManager.Api.Tests.Series.SeriesHarness;
using static MW_GC.EventManager.Api.Tests.Series.SeriesTestSupport;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// <c>PUT /api/series/{id}?scope=following&amp;occurrence={eventId}</c> (#204): the old Series ends just before
/// the chosen Occurrence, a new Series with the change starts there, and the future, unmodified, unplayed
/// Occurrences from it on move to the new Series; Modified, played and earlier ones stay where they are.
/// </summary>
[TestClass]
public sealed class SeriesEditFollowingTests
{
    private const string OccurrenceMessage = "Occurrence must name an Occurrence of this Series.";

    private readonly SeriesHarness h = new();

    private static string Following(Guid occurrence) => $"?scope=following&occurrence={occurrence:D}";

    [TestMethod]
    public async Task The_old_Series_ends_just_before_the_chosen_Occurrence_and_a_new_one_starts_there()
    {
        var (id, _, _) = await h.CreateAsync(key: Guid.Parse("11111111-2222-3333-4444-555555555555"));
        var fourth = (await h.OccurrencesAsync(id))[3];

        var change = await h.EditAsync(id, Body(name: "Late night", first: "2027-03-25T21:00:00-05:00"), Following(fourth.Id));

        var old = (await h.SeriesAsync(id))!;
        Assert.AreEqual("2027-03-25T18:59:59-05:00", Local(old.Schedule.End!.Value));
        Assert.AreEqual("Thursday night", old.Name);
        var newId = change.NewSeries!.Id;
        // series-following:11111111-2222-3333-4444-555555555555:639416160000000000, computed outside .NET.
        Assert.AreEqual(Guid.Parse("ba596c9b-cb9b-8f6e-4bad-8796702ba904"), newId);
        var created = (await h.SeriesAsync(newId))!;
        Assert.AreEqual("Late night", created.Name);
        Assert.AreEqual(At("2027-03-25T21:00:00-05:00"), created.Schedule.First);
        Assert.IsNull(created.Schedule.End);

        CollectionAssert.AreEqual(
            new[] { "2027-03-04T19:00:00-05:00", "2027-03-11T19:00:00-05:00", "2027-03-18T19:00:00-05:00" },
            Dates(await h.OccurrencesAsync(id)));
        var moved = await h.OccurrencesAsync(newId);
        CollectionAssert.AreEqual(
            new[]
            {
                "2027-03-25T21:00:00-05:00", "2027-04-01T21:00:00-05:00", "2027-04-08T21:00:00-05:00",
                "2027-04-15T21:00:00-05:00", "2027-04-22T21:00:00-05:00",
            },
            Dates(moved));
        Assert.IsTrue(moved.All(o => o.Name == "Late night" && o.Selections.Count == 0 && !o.Modified));
        Assert.AreEqual(5, change.Deleted);
        Assert.AreEqual(5, change.Created);
    }

    [TestMethod]
    public async Task A_Modified_Occurrence_after_the_chosen_one_stays_with_the_old_Series_and_gets_no_twin()
    {
        var (id, _, _) = await h.CreateAsync();
        var occurrences = await h.OccurrencesAsync(id);
        var sixth = await h.EditOneAsync(occurrences[5], "Special");

        var change = await h.EditAsync(id, Body(name: "Late night", first: "2027-03-25T21:00:00-05:00"), Following(occurrences[3].Id));

        var kept = (await h.EventsAsync()).Single(o => o.Id == sixth.Id);
        Assert.AreEqual(id, kept.SeriesId);
        Assert.AreEqual("Special", kept.Name);
        Assert.AreEqual("2027-04-08T19:00:00-05:00", Local(kept.Date));
        var moved = await h.OccurrencesAsync(change.NewSeries!.Id);
        Assert.IsFalse(Dates(moved).Any(d => d.StartsWith("2027-04-08", StringComparison.Ordinal)), string.Join(", ", Dates(moved)));
        Assert.HasCount(4, moved);
    }

    [TestMethod]
    [DataRow("winner")]
    [DataRow("comment")]
    [DataRow("metrics")]
    public async Task A_played_Occurrence_after_the_chosen_one_stays_with_the_old_Series(string reason)
    {
        var (id, _, _) = await h.CreateAsync();
        var occurrences = await h.OccurrencesAsync(id);
        h.MakePlayed(occurrences[4].Id, reason);

        var change = await h.EditAsync(id, Body(name: "Late night", first: "2027-03-25T21:00:00-05:00"), Following(occurrences[3].Id));

        var kept = (await h.EventsAsync()).Single(o => o.Id == occurrences[4].Id);
        Assert.AreEqual(id, kept.SeriesId);
        Assert.AreEqual("Thursday night", kept.Name);
        Assert.AreEqual("2027-04-01T19:00:00-05:00", Local(kept.Date));
        Assert.IsFalse(Dates(await h.OccurrencesAsync(change.NewSeries!.Id)).Any(d => d.StartsWith("2027-04-01", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Choosing_a_past_Occurrence_moves_only_future_ones()
    {
        var (id, _, _) = await h.CreateAsync();
        var occurrences = await h.OccurrencesAsync(id);
        h.Clock.Now = At("2027-03-19T12:00:00-05:00");

        var change = await h.EditAsync(id, Body(name: "Late night", first: "2027-03-11T21:00:00-05:00"), Following(occurrences[1].Id));

        var events = await h.EventsAsync();
        var stayed = events.Where(e => e.SeriesId == id).ToList();
        CollectionAssert.AreEqual(new[] { "2027-03-04T19:00:00-05:00", "2027-03-11T19:00:00-05:00", "2027-03-18T19:00:00-05:00" }, Dates(stayed));
        Assert.IsTrue(stayed.All(e => e.Name == "Thursday night"));
        var moved = events.Where(e => e.SeriesId == change.NewSeries!.Id).ToList();
        Assert.IsTrue(moved.All(e => e.Date > h.Clock.Now && e.Name == "Late night"), string.Join(", ", Dates(moved)));
        Assert.AreEqual("2027-03-25T21:00:00-05:00", Dates(moved)[0]);
    }

    [TestMethod]
    public async Task Repeating_a_following_edit_whose_Occurrence_moved_is_refused_and_makes_nothing_twice()
    {
        var (id, _, _) = await h.CreateAsync();
        var fourth = (await h.OccurrencesAsync(id))[3];
        var body = Body(name: "Late night", first: "2027-03-25T21:00:00-05:00");
        await h.EditAsync(id, body, Following(fourth.Id));

        // The chosen Occurrence moved to the new Series as a new row, so the same request names nothing now.
        var again = await h.SeriesApi.Update(Request(body, query: Following(fourth.Id)), id, default);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(again));
        Assert.AreEqual(OccurrenceMessage, ApiResults.Message(again));
        Assert.HasCount(2, h.SeriesRows);
        Assert.HasCount(8, h.Events);
    }

    [TestMethod]
    public async Task Repeating_a_following_edit_from_a_Modified_Occurrence_reuses_the_new_Series()
    {
        var (id, _, _) = await h.CreateAsync();
        var fourth = await h.EditOneAsync((await h.OccurrencesAsync(id))[3], "Special");
        var body = Body(name: "Late night", first: "2027-03-25T21:00:00-05:00");
        var change = await h.EditAsync(id, body, Following(fourth.Id));

        var again = await h.EditAsync(id, body, Following(fourth.Id));

        Assert.AreEqual(change.NewSeries!.Id, again.NewSeries!.Id);
        Assert.AreEqual(0, again.Created);
        Assert.AreEqual(0, again.Deleted);
        Assert.HasCount(2, h.SeriesRows);
        Assert.HasCount(8, h.Events);
        Assert.AreEqual("Special", (await h.EventsAsync()).Single(e => e.Id == fourth.Id).Name);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("not-a-guid")]
    [DataRow("unknown")]
    [DataRow("other-series")]
    public async Task A_following_edit_must_name_an_Occurrence_of_this_Series(string which)
    {
        var (id, _, _) = await h.CreateAsync();
        var (other, _, _) = await h.CreateAsync(Body(name: "Other"));
        var query = which switch
        {
            "" => "?scope=following",
            "not-a-guid" => "?scope=following&occurrence=xyz",
            "unknown" => Following(Guid.NewGuid()),
            _ => Following((await h.OccurrencesAsync(other))[2].Id),
        };

        var result = await h.SeriesApi.Update(Request(Body(name: "Late night"), query: query), id, default);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(result));
        Assert.AreEqual(OccurrenceMessage, ApiResults.Message(result));
        Assert.IsNull((await h.SeriesAsync(id))!.Schedule.End);
        Assert.HasCount(2, h.SeriesRows);
    }

    [TestMethod]
    public async Task Following_from_the_first_Occurrence_is_refused_and_changes_nothing()
    {
        var (id, _, _) = await h.CreateAsync();
        var first = (await h.OccurrencesAsync(id))[0];

        var result = await h.SeriesApi.Update(Request(Body(name: "Late night"), query: Following(first.Id)), id, default);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(result));
        Assert.AreEqual("This and following from the first Occurrence changes the whole Series: use scope all.", ApiResults.Message(result));
        Assert.IsNull((await h.SeriesAsync(id))!.Schedule.End);
        Assert.HasCount(1, h.SeriesRows);
        Assert.IsTrue((await h.OccurrencesAsync(id)).All(o => o.Name == "Thursday night"));
    }

    [TestMethod]
    public async Task A_new_start_on_or_before_the_Occurrence_before_the_chosen_one_is_refused()
    {
        var (id, _, _) = await h.CreateAsync();
        var fourth = (await h.OccurrencesAsync(id))[3];

        var result = await h.SeriesApi.Update(Request(Body(first: "2027-03-18T19:00:00-05:00"), query: Following(fourth.Id)), id, default);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(result));
        Assert.AreEqual("Start must be after the Occurrence before the one this change starts from.", ApiResults.Message(result));
        Assert.IsNull((await h.SeriesAsync(id))!.Schedule.End);
    }

    [TestMethod]
    public async Task A_stale_If_Match_is_a_409_and_starts_no_new_Series()
    {
        var (id, _, original) = await h.CreateAsync();
        await h.EditAsync(id, Body(name: "First edit"), "?scope=all", ifMatch: original);
        var fourth = (await h.OccurrencesAsync(id))[3];

        var result = await h.SeriesApi.Update(
            Request(Body(name: "Late night", first: "2027-03-25T21:00:00-05:00"), original, Following(fourth.Id)), id, default);

        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(result));
        Assert.HasCount(1, h.SeriesRows);
        Assert.IsNull((await h.SeriesAsync(id))!.Schedule.End);
        Assert.IsTrue((await h.OccurrencesAsync(id)).All(o => o.Name == "First edit"));
    }
}
