using Microsoft.AspNetCore.Http;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using static MW_GC.EventManager.Api.Tests.Series.SeriesHarness;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// One Occurrence through the Event routes (#204): it may have zero Selections where a standalone Event may
/// not, editing it by hand (the "this occurrence" scope) marks it Modified, and deleting it cancels it for
/// good by recording its key on its Series.
/// </summary>
[TestClass]
public sealed class OccurrenceRouteTests
{
    private readonly SeriesHarness h = new();

    private static object EventBody(string name, object[] selections, Guid? seriesId = null) => new
    {
        name, date = "2027-03-04T19:00:00-05:00", uniqueGamesOnly = true, selections, seriesId,
    };

    [TestMethod]
    public async Task An_Occurrence_with_no_Selections_round_trips_through_get_list_and_put()
    {
        var (id, _, _) = await h.CreateAsync();
        var first = (await h.OccurrencesAsync(id))[0];

        var request = Bare();
        var one = ApiResults.Value<EventEntity>(await h.EventApi.Get(request, first.Id, default));
        Assert.IsEmpty(one.Selections);
        Assert.AreEqual(id, one.SeriesId);
        Assert.IsNotNull(ETagHeader(request));

        Assert.IsTrue((await h.EventsAsync()).Any(e => e.Id == first.Id && e.Selections.Count == 0));

        var put = await h.EventApi.Update(Request(EventBody("Still empty", [])), first.Id, default);
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(put));
        var saved = ApiResults.Value<EventEntity>(put);
        Assert.IsEmpty(saved.Selections);
        Assert.IsNull(saved.WinnerActivityId);
    }

    [TestMethod]
    public async Task A_standalone_Event_with_no_Selections_is_still_refused_even_when_its_body_claims_a_Series()
    {
        foreach (var seriesId in new Guid?[] { null, Guid.NewGuid() })
        {
            var result = await h.EventApi.SaveCustomized(Request(EventBody("Empty", [], seriesId)), default);
            Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(result));
            Assert.AreEqual("Select between 1 and 5 activities.", ApiResults.Message(result));
        }
        Assert.IsEmpty(h.Events);
    }

    [TestMethod]
    public async Task A_standalone_Event_put_with_no_Selections_is_refused_even_when_its_body_claims_a_Series()
    {
        var game = new Game { Id = Guid.NewGuid(), Name = "Alpha" };
        var one = new[] { new Selection { Game = game, Activity = new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Raid" } } };
        var created = ApiResults.Value<EventEntity>(await h.EventApi.SaveCustomized(Request(EventBody("Solo", one)), default));
        Assert.IsNull(created.SeriesId);

        var put = await h.EventApi.Update(Request(EventBody("Solo", [], Guid.NewGuid())), created.Id, default);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(put));
        Assert.AreEqual("Select between 1 and 5 activities.", ApiResults.Message(put));
        Assert.IsNull(h.Events[created.Id.ToString("D")].GetGuid("SeriesId"));
        Assert.IsNull(ApiResults.Value<EventEntity>(await h.EventApi.Get(Bare(), created.Id, default)).SeriesId);
    }

    [TestMethod]
    public async Task Editing_one_Occurrence_marks_it_Modified_and_keeps_its_Series_and_key()
    {
        var (id, _, _) = await h.CreateAsync();
        var second = (await h.OccurrencesAsync(id))[1];

        var body = new { name = "Special", date = "2027-03-12T20:00:00-05:00", uniqueGamesOnly = true, selections = Array.Empty<object>(),
            seriesId = Guid.NewGuid(), occurrenceKey = "2030-01-01T00:00:00Z", modified = false };
        var saved = ApiResults.Value<EventEntity>(await h.EventApi.Update(Request(body), second.Id, default));

        Assert.IsTrue(saved.Modified);
        Assert.AreEqual(id, saved.SeriesId);
        Assert.AreEqual(second.OccurrenceKey, saved.OccurrenceKey);
        var others = (await h.OccurrencesAsync(id)).Where(o => o.Id != second.Id).ToList();
        Assert.HasCount(7, others);
        Assert.IsTrue(others.All(o => !o.Modified && o.Name == "Thursday night"));
    }

    [TestMethod]
    public async Task Editing_a_standalone_Event_never_marks_it_Modified()
    {
        var game = new Game { Id = Guid.NewGuid(), Name = "Alpha" };
        var one = new[] { new Selection { Game = game, Activity = new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Raid" } } };
        var created = ApiResults.Value<EventEntity>(await h.EventApi.SaveCustomized(Request(EventBody("Solo", one)), default));

        var saved = ApiResults.Value<EventEntity>(await h.EventApi.Update(Request(EventBody("Solo again", one)), created.Id, default));

        Assert.IsFalse(saved.Modified);
        Assert.IsNull(saved.SeriesId);
        Assert.IsFalse(h.Events[created.Id.ToString("D")].GetBoolean("Modified") ?? false);
    }

    [TestMethod]
    public async Task Deleting_a_standalone_Event_writes_no_Series_and_keeps_the_If_Match_rules()
    {
        var (id, _, _) = await h.CreateAsync();
        var solo = await h.CreateStandaloneAsync();
        var stale = solo.ETag.ToString();
        var edited = ApiResults.Value<EventEntity>(await h.EventApi.Update(
            Request(new { name = "Solo again", date = solo.Date, uniqueGamesOnly = true, selections = solo.Selections }), solo.Id, default));
        var seriesWrites = h.SeriesTable.Invocations.Count(i => i.Method.Name == nameof(Azure.Data.Tables.TableClient.UpdateEntityAsync));

        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(await h.EventApi.Delete(Bare(stale), solo.Id, default)));
        Assert.AreEqual(StatusCodes.Status204NoContent, ApiResults.Status(await h.EventApi.Delete(Bare(edited.ETag.ToString()), solo.Id, default)));
        Assert.AreEqual(StatusCodes.Status204NoContent, ApiResults.Status(await h.EventApi.Delete(Bare(), solo.Id, default)));

        Assert.IsFalse(h.Events.ContainsKey(solo.Id.ToString("D")));
        Assert.AreEqual(seriesWrites, h.SeriesTable.Invocations.Count(i => i.Method.Name == nameof(Azure.Data.Tables.TableClient.UpdateEntityAsync)));
        Assert.IsEmpty((await h.SeriesAsync(id))!.Skipped);
    }

    [TestMethod]
    public async Task Cancelling_one_Occurrence_deletes_it_and_records_its_key_on_the_Series()
    {
        var (id, _, _) = await h.CreateAsync();
        var third = (await h.OccurrencesAsync(id))[2];

        Assert.AreEqual(StatusCodes.Status204NoContent, ApiResults.Status(await h.EventApi.Delete(Bare(), third.Id, default)));

        Assert.HasCount(7, await h.OccurrencesAsync(id));
        Assert.AreEqual(third.OccurrenceKey, Assert.ContainsSingle((await h.SeriesAsync(id))!.Skipped));
    }

    [TestMethod]
    public async Task A_cancelled_Occurrence_is_not_made_again_by_a_repeated_create()
    {
        var key = Guid.NewGuid();
        var (id, _, _) = await h.CreateAsync(key: key);
        var third = (await h.OccurrencesAsync(id))[2];
        await h.EventApi.Delete(Bare(), third.Id, default);

        var again = await h.SeriesApi.Create(Request(Body(), key: key), default);

        Assert.AreEqual(0, ApiResults.Value<MW_GC.EventManager.Shared.Requests.SeriesChange>(again).Created);
        CollectionAssert.DoesNotContain(Dates(await h.OccurrencesAsync(id)), "2027-03-18T19:00:00-05:00");
    }

    [TestMethod]
    public async Task A_stale_If_Match_on_an_Occurrence_cancel_records_nothing_and_deletes_nothing()
    {
        var (id, _, _) = await h.CreateAsync();
        var third = (await h.OccurrencesAsync(id))[2];
        var stale = third.ETag.ToString();
        await h.EditOneAsync(third, "Edited meanwhile");

        var result = await h.EventApi.Delete(Bare(stale), third.Id, default);

        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(result));
        Assert.HasCount(8, await h.OccurrencesAsync(id));
        Assert.IsEmpty((await h.SeriesAsync(id))!.Skipped);
    }
}
