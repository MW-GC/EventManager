using Microsoft.AspNetCore.Http;
using MW_GC.EventManager.Shared.Entities;
using static MW_GC.EventManager.Api.Tests.Series.SeriesHarness;
using static MW_GC.EventManager.Api.Tests.Series.SeriesTestSupport;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// <c>PUT /api/series/{id}?scope=all</c> (#204): the change reaches every Occurrence that is in the future,
/// not Modified and not played; past, Modified and played ones are never rewritten, and no second Occurrence
/// is made on a date one of them holds or a cancelled one had.
/// </summary>
[TestClass]
public sealed class SeriesEditAllTests
{
    private const string All = "?scope=all";
    private readonly SeriesHarness h = new();

    [TestMethod]
    public async Task A_rename_rewrites_every_future_Occurrence_in_place()
    {
        var (id, _, _) = await h.CreateAsync();
        var before = await h.OccurrencesAsync(id);

        var change = await h.EditAsync(id, Body(name: "Game night"), All);

        Assert.AreEqual(8, change.Updated);
        Assert.AreEqual(0, change.Created);
        Assert.AreEqual(0, change.Deleted);
        var after = await h.OccurrencesAsync(id);
        CollectionAssert.AreEqual(before.Select(o => o.Id).ToList(), after.Select(o => o.Id).ToList());
        Assert.IsTrue(after.All(o => o.Name == "Game night" && !o.Modified), string.Join(", ", after.Select(o => o.Name)));
        Assert.AreEqual("Game night", (await h.SeriesAsync(id))!.Name);
    }

    [TestMethod]
    public async Task A_new_time_moves_every_future_Occurrence()
    {
        var (id, _, _) = await h.CreateAsync();

        await h.EditAsync(id, Body(first: "2027-03-04T20:00:00-05:00"), All);

        CollectionAssert.AreEqual(
            new[]
            {
                "2027-03-04T20:00:00-05:00", "2027-03-11T20:00:00-05:00", "2027-03-18T20:00:00-05:00", "2027-03-25T20:00:00-05:00",
                "2027-04-01T20:00:00-05:00", "2027-04-08T20:00:00-05:00", "2027-04-15T20:00:00-05:00", "2027-04-22T20:00:00-05:00",
            },
            Dates(await h.OccurrencesAsync(id)));
    }

    [TestMethod]
    public async Task Past_Occurrences_are_never_rewritten()
    {
        var (id, _, _) = await h.CreateAsync();
        h.Clock.Now = At("2027-03-12T12:00:00-05:00");

        await h.EditAsync(id, Body(name: "Game night", first: "2027-03-04T20:00:00-05:00"), All);

        var occurrences = await h.OccurrencesAsync(id);
        var past = occurrences.Where(o => o.Date < h.Clock.Now).ToList();
        CollectionAssert.AreEqual(new[] { "2027-03-04T19:00:00-05:00", "2027-03-11T19:00:00-05:00" }, Dates(past));
        Assert.IsTrue(past.All(o => o.Name == "Thursday night"), string.Join(", ", past.Select(o => o.Name)));
        Assert.IsTrue(occurrences.Except(past).All(o => o.Name == "Game night"), string.Join(", ", occurrences.Select(o => o.Name)));
        CollectionAssert.AreEqual(
            new[]
            {
                "2027-03-18T20:00:00-05:00", "2027-03-25T20:00:00-05:00", "2027-04-01T20:00:00-05:00", "2027-04-08T20:00:00-05:00",
                "2027-04-15T20:00:00-05:00", "2027-04-22T20:00:00-05:00", "2027-04-29T20:00:00-05:00", "2027-05-06T20:00:00-05:00",
            },
            Dates(occurrences.Except(past)));
    }

    [TestMethod]
    public async Task A_Modified_Occurrence_is_left_alone_and_gets_no_twin()
    {
        var (id, _, _) = await h.CreateAsync();
        var second = (await h.OccurrencesAsync(id))[1];
        await h.EditOneAsync(second, "Special");

        await h.EditAsync(id, Body(name: "Game night", first: "2027-03-04T20:00:00-05:00"), All);

        var occurrences = await h.OccurrencesAsync(id);
        var kept = occurrences.Single(o => o.Id == second.Id);
        Assert.AreEqual("Special", kept.Name);
        Assert.AreEqual("2027-03-11T19:00:00-05:00", Local(kept.Date));
        Assert.IsTrue(kept.Modified);
        Assert.AreEqual(1, occurrences.Count(o => Local(o.Date).StartsWith("2027-03-11", StringComparison.Ordinal)), string.Join(", ", Dates(occurrences)));
        Assert.HasCount(8, occurrences);
    }

    [TestMethod]
    [DataRow("winner")]
    [DataRow("comment")]
    [DataRow("metrics")]
    public async Task A_played_Occurrence_is_left_alone_and_gets_no_twin(string reason)
    {
        var (id, _, _) = await h.CreateAsync();
        var third = (await h.OccurrencesAsync(id))[2];
        h.MakePlayed(third.Id, reason);

        await h.EditAsync(id, Body(name: "Game night", first: "2027-03-04T20:00:00-05:00"), All);

        var occurrences = await h.OccurrencesAsync(id);
        var kept = occurrences.Single(o => o.Id == third.Id);
        Assert.AreEqual("Thursday night", kept.Name);
        Assert.AreEqual("2027-03-18T19:00:00-05:00", Local(kept.Date));
        Assert.AreEqual(1, occurrences.Count(o => Local(o.Date).StartsWith("2027-03-18", StringComparison.Ordinal)), string.Join(", ", Dates(occurrences)));
        Assert.HasCount(8, occurrences);
    }

    [TestMethod]
    [DataRow("winner")]
    [DataRow("comment")]
    [DataRow("metrics")]
    public async Task A_rename_never_rewrites_a_played_Occurrence_in_place(string reason)
    {
        // Same rule and time: every other Occurrence is rewritten where it stands, not deleted.
        var (id, _, _) = await h.CreateAsync();
        var third = (await h.OccurrencesAsync(id))[2];
        h.MakePlayed(third.Id, reason);

        var change = await h.EditAsync(id, Body(name: "Game night"), All);

        var occurrences = await h.OccurrencesAsync(id);
        Assert.AreEqual("Thursday night", occurrences.Single(o => o.Id == third.Id).Name);
        Assert.AreEqual(7, change.Updated);
        Assert.HasCount(7, occurrences.Where(o => o.Name == "Game night"));
    }

    [TestMethod]
    public async Task Event_metrics_with_every_value_cleared_do_not_make_an_Occurrence_played()
    {
        var (id, _, _) = await h.CreateAsync();
        var third = (await h.OccurrencesAsync(id))[2];
        h.SeedMetrics(third.Id, attendees: null);

        await h.EditAsync(id, Body(name: "Game night"), All);

        Assert.AreEqual("Game night", (await h.OccurrencesAsync(id)).Single(o => o.Id == third.Id).Name);
    }

    [TestMethod]
    public async Task A_cancelled_Occurrence_does_not_come_back_after_rule_edits()
    {
        var (id, _, _) = await h.CreateAsync();
        var third = (await h.OccurrencesAsync(id))[2];
        Assert.AreEqual(StatusCodes.Status204NoContent, ApiResults.Status(await h.EventApi.Delete(Bare(), third.Id, default)));

        // Same rule, new name: the exact key is skipped.
        await h.EditAsync(id, Body(name: "Game night"), All);
        Assert.IsFalse(Dates(await h.OccurrencesAsync(id)).Any(d => d.StartsWith("2027-03-18", StringComparison.Ordinal)));
        // A new time of day on the same dates: the cancelled date stays cancelled.
        await h.EditAsync(id, Body(name: "Game night", first: "2027-03-04T20:00:00-05:00"), All);
        Assert.IsFalse(Dates(await h.OccurrencesAsync(id)).Any(d => d.StartsWith("2027-03-18", StringComparison.Ordinal)));
        // Every other week from the 4th still produces the 18th: still cancelled.
        await h.EditAsync(id, Body(name: "Game night", first: "2027-03-04T20:00:00-05:00", rule: new { kind = "BiWeekly" }), All);

        var occurrences = await h.OccurrencesAsync(id);
        Assert.IsFalse(Dates(occurrences).Any(d => d.StartsWith("2027-03-18", StringComparison.Ordinal)), string.Join(", ", Dates(occurrences)));
        // 29 April 20:00 is past now plus 8 weeks (29 April 12:00), so it waits.
        CollectionAssert.AreEqual(
            new[] { "2027-03-04T20:00:00-05:00", "2027-04-01T20:00:00-05:00", "2027-04-15T20:00:00-05:00" },
            Dates(occurrences));
    }

    [TestMethod]
    public async Task A_stale_If_Match_is_a_409_and_changes_nothing()
    {
        var (id, _, original) = await h.CreateAsync();
        var current = await h.EditAsync(id, Body(name: "First edit"), All, ifMatch: original);
        var before = (await h.OccurrencesAsync(id)).Select(o => (o.Id, o.Name, o.Date)).ToList();

        var result = await h.SeriesApi.Update(Request(Body(name: "Second edit", first: "2027-03-04T20:00:00-05:00"), original, All), id, default);

        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(result));
        Assert.AreEqual("The request conflicts with the stored data. Reload and try again.", ApiResults.Message(result));
        Assert.AreEqual("First edit", (await h.SeriesAsync(id))!.Name);
        CollectionAssert.AreEqual(before, (await h.OccurrencesAsync(id)).Select(o => (o.Id, o.Name, o.Date)).ToList());
        Assert.AreNotEqual(original, current.Series!.ETag.ToString());
    }

    [TestMethod]
    public async Task An_edit_answers_with_the_new_ETag()
    {
        var (id, _, original) = await h.CreateAsync();
        var request = Request(Body(name: "Game night"), original, All);

        var result = await h.SeriesApi.Update(request, id, default);

        var etag = ETagHeader(request);
        Assert.IsNotNull(etag);
        Assert.AreNotEqual(original, etag);
        Assert.AreEqual(etag, ApiResults.Value<MW_GC.EventManager.Shared.Requests.SeriesChange>(result).Series!.ETag.ToString());
        Assert.AreEqual(etag, (await h.SeriesAsync(id))!.ETag.ToString());
    }

    [TestMethod]
    public async Task An_edit_keeps_the_cancelled_keys_whatever_the_body_says()
    {
        var (id, _, _) = await h.CreateAsync();
        var third = (await h.OccurrencesAsync(id))[2];
        await h.EventApi.Delete(Bare(), third.Id, default);
        var body = System.Text.Json.JsonSerializer.SerializeToNode(Body(name: "Game night"), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        body["skipped"] = new System.Text.Json.Nodes.JsonArray();

        var result = await h.SeriesApi.Update(SeriesHarness.Decorated(TestRequests.Raw(body.ToJsonString()), All), id, default);

        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(result));
        CollectionAssert.Contains((await h.SeriesAsync(id))!.Skipped, third.OccurrenceKey!.Value);
    }
}
