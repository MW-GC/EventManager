using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MW_GC.EventManager.API.Functions;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Requests;
using static MW_GC.EventManager.Api.Tests.Series.SeriesHarness;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// <c>POST /api/series</c> (#204): it stores the Series and creates its Occurrences for now, each an Event
/// with no Selections yet; a repeated create, a second run and an overlapping run make nothing twice.
/// </summary>
[TestClass]
public sealed class SeriesCreateTests
{
    private static readonly string[] EightThursdays =
    [
        "2027-03-04T19:00:00-05:00", "2027-03-11T19:00:00-05:00", "2027-03-18T19:00:00-05:00", "2027-03-25T19:00:00-05:00",
        "2027-04-01T19:00:00-05:00", "2027-04-08T19:00:00-05:00", "2027-04-15T19:00:00-05:00", "2027-04-22T19:00:00-05:00",
    ];

    private readonly SeriesHarness h = new();

    private int EventInserts() => h.EventTable.Invocations.Count(i => i.Method.Name == nameof(Azure.Data.Tables.TableClient.AddEntityAsync));

    [TestMethod]
    public async Task A_new_weekly_Series_creates_its_next_eight_Occurrences_with_no_Selections()
    {
        // A fixed key, so the first Occurrence's id is the literal pinned in OccurrenceIdTests.
        var request = Request(Body(), key: Guid.Parse("11111111-2222-3333-4444-555555555555"));
        var result = await h.SeriesApi.Create(request, default);

        Assert.AreEqual(StatusCodes.Status201Created, ApiResults.Status(result));
        var change = ApiResults.Value<SeriesChange>(result);
        Assert.AreEqual(8, change.Created);
        var id = change.Series!.Id;
        Assert.AreEqual($"/api/series/{id}", Assert.IsInstanceOfType<CreatedResult>(result).Location);
        Assert.AreEqual(change.Series.ETag.ToString(), ETagHeader(request));

        var occurrences = await h.OccurrencesAsync(id);
        CollectionAssert.AreEqual(EightThursdays, Dates(occurrences));
        foreach (var occurrence in occurrences)
        {
            Assert.IsEmpty(occurrence.Selections);
            Assert.IsNull(occurrence.WinnerActivityId);
            Assert.AreEqual("Thursday night", occurrence.Name);
            Assert.AreEqual(occurrence.Date, occurrence.OccurrenceKey);
            Assert.IsFalse(occurrence.Modified);
        }
        Assert.AreEqual(Guid.Parse("977e2ff9-168d-89b8-c28c-b2c036ff7549"), occurrences[0].Id);
        Assert.AreEqual(Guid.Parse("e133287e-6af8-4ede-a992-448dc163758b"), occurrences[1].Id);
    }

    [TestMethod]
    public async Task A_repeated_create_with_the_same_key_and_body_adds_nothing()
    {
        var key = Guid.NewGuid();
        var (id, _, _) = await h.CreateAsync(key: key);
        var inserts = EventInserts();

        var again = await h.SeriesApi.Create(Request(Body(), key: key), default);

        // The existing keys reach the generator, so it passes nothing and no insert is even tried.
        Assert.AreEqual(inserts, EventInserts());
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(again));
        Assert.AreEqual(0, ApiResults.Value<SeriesChange>(again).Created);
        Assert.AreEqual(id, ApiResults.Value<SeriesChange>(again).Series!.Id);
        Assert.HasCount(1, h.SeriesRows);
        CollectionAssert.AreEqual(EightThursdays, Dates(await h.EventsAsync()));
    }

    [TestMethod]
    public async Task A_repeated_create_after_a_partial_first_run_makes_only_the_missing_Occurrences()
    {
        var key = Guid.NewGuid();
        var (id, _, _) = await h.CreateAsync(key: key);
        // As if the first run stopped after its fifth insert.
        foreach (var occurrence in (await h.OccurrencesAsync(id)).Skip(5))
            h.Events.TryRemove(occurrence.Id.ToString("D"), out _);

        var again = await h.SeriesApi.Create(Request(Body(), key: key), default);

        Assert.AreEqual(3, ApiResults.Value<SeriesChange>(again).Created);
        CollectionAssert.AreEqual(EightThursdays, Dates(await h.OccurrencesAsync(id)));
    }

    [TestMethod]
    public async Task An_overlapping_run_that_misses_the_existing_keys_still_neither_duplicates_nor_overwrites()
    {
        var (id, _, _) = await h.CreateAsync();
        var first = (await h.OccurrencesAsync(id))[0];
        await h.EditOneAsync(first, "Renamed by hand");
        var series = await h.SeriesAsync(id);

        // A second run that did not see any existing Occurrence: every insert hits an existing id.
        var done = await h.Occurrences.ReconcileAsync(series!, [], [], h.Clock.GetUtcNow(), default);

        Assert.AreEqual(0, done.Created);
        var occurrences = await h.OccurrencesAsync(id);
        CollectionAssert.AreEqual(EightThursdays, Dates(occurrences));
        Assert.AreEqual("Renamed by hand", occurrences[0].Name);
    }

    [TestMethod]
    public async Task A_repeated_create_with_the_same_key_and_other_details_is_a_409_and_writes_nothing()
    {
        var key = Guid.NewGuid();
        await h.CreateAsync(key: key);
        var before = JsonSerializer.Serialize(h.SeriesRows.Values.Single());

        var changed = await h.SeriesApi.Create(Request(Body(name: "Friday night"), key: key), default);

        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(changed));
        Assert.AreEqual("This create key already exists with different details. Reload the Series list and edit the saved Series; do not start another create to retry this save.",
            ApiResults.Message(changed));
        Assert.AreEqual(before, JsonSerializer.Serialize(h.SeriesRows.Values.Single()));
        Assert.HasCount(8, h.Events);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("not-a-guid")]
    [DataRow("00000000-0000-0000-0000-000000000000")]
    public async Task A_create_needs_an_Idempotency_Key(string? key)
    {
        var request = TestRequests.Json(Body());
        if (key is not null) request.Headers["Idempotency-Key"] = key;

        var result = await h.SeriesApi.Create(request, default);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(result));
        Assert.AreEqual("Idempotency-Key must be a non-empty GUID in D format.", ApiResults.Message(result));
        Assert.IsEmpty(h.SeriesRows);
        Assert.IsEmpty(h.Events);
    }

    [TestMethod]
    public async Task A_create_ignores_cancelled_keys_in_its_body()
    {
        var body = JsonSerializer.SerializeToNode(Body(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        body["skipped"] = new System.Text.Json.Nodes.JsonArray("2027-03-11T19:00:00-05:00");

        var result = await h.SeriesApi.Create(Raw(body.ToJsonString(), Guid.NewGuid()), default);

        Assert.AreEqual(8, ApiResults.Value<SeriesChange>(result).Created);
        Assert.IsEmpty(ApiResults.Value<SeriesChange>(result).Series!.Skipped);
    }

    [TestMethod]
    public async Task A_Series_reads_back_one_by_one_and_in_the_list_with_its_ETag()
    {
        var (id, change, etag) = await h.CreateAsync();

        var request = Bare();
        var one = ApiResults.Value<SeriesEntity>(await h.SeriesApi.Get(request, id, default));
        Assert.AreEqual(etag, ETagHeader(request));
        Assert.AreEqual("Thursday night", one.Name);
        Assert.AreEqual(SeriesTestSupport.At(First), one.Schedule.First);
        Assert.AreEqual(Offset, one.Schedule.First.Offset);

        var all = ApiResults.Value<List<SeriesEntity>>(await h.SeriesApi.GetAll(Bare(), default));
        Assert.AreEqual(id, Assert.ContainsSingle(all).Id);
        Assert.AreEqual(change.Series!.ETag, all[0].ETag);
    }

    [TestMethod]
    public async Task An_unknown_Series_is_a_404_to_read_and_to_edit()
    {
        Assert.AreEqual(StatusCodes.Status404NotFound, ApiResults.Status(await h.SeriesApi.Get(Bare(), Guid.NewGuid(), default)));
        Assert.AreEqual(StatusCodes.Status404NotFound,
            ApiResults.Status(await h.SeriesApi.Update(Request(Body(), query: "?scope=all"), Guid.NewGuid(), default)));
        Assert.IsEmpty(h.SeriesRows);
    }
}
