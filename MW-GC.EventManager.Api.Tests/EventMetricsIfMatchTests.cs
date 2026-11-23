using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Moq;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>Event metrics PUT and If-Match (#134): only a conditional replace with the current tag overwrites a stored row.</summary>
[TestClass]
public sealed class EventMetricsIfMatchTests : EventMetricsTestBase
{
    // ---- If-Match

    [TestMethod]
    public async Task Put_WithAStaleIfMatch_Is409_AndLeavesTheRowUnchanged()
    {
        var (id, _) = await CreateEvent();
        var (_, original) = await Put(id, Metrics(10));
        await Put(id, Metrics(20), original); // another admin saves first

        // The second admin still holds the original ETag.
        var result = await h.EventMetricsApi.PutEventMetrics(Json(Metrics(40), original), id, default);

        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(result));
        Assert.AreEqual(RouteFailures.ConflictMessage, ApiResults.Message(result));
        Assert.AreEqual(20, (await Get(id)).Metrics.Attendees);
    }

    [TestMethod]
    public async Task Put_WithIfMatch_WhenNoRowIsStored_Is409_AndCreatesNothing()
    {
        var (id, eventTag) = await CreateEvent();

        var tagged = await h.EventMetricsApi.PutEventMetrics(Json(Metrics(10), eventTag), id, default);
        var star = await h.EventMetricsApi.PutEventMetrics(Json(Metrics(10), "*"), id, default);

        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(tagged));
        Assert.AreEqual(RouteFailures.ConflictMessage, ApiResults.Message(tagged));
        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(star));
        Assert.IsEmpty(h.EventMetrics);
        // An If-Match is only ever a conditional replace, which storage refuses for a missing row;
        // nothing tries to insert or upsert one.
        Assert.AreEqual(0, h.EventMetricsTable.Invocations.Count(i => i.Method.Name is nameof(TableClient.AddEntityAsync)
            or nameof(TableClient.UpsertEntityAsync)));
    }

    [TestMethod]
    public async Task Put_WithoutIfMatch_WhenNoRowIsStored_Creates_200_WithETag()
    {
        var (id, _) = await CreateEvent();

        var (written, putTag) = await Put(id, Metrics(10, 2, 8, 50m));
        var (read, getTag) = await Get(id);

        Assert.AreEqual(putTag, written.ETag.ToString());
        AssertValues(read, 10, 2, 8, 50m);
        Assert.AreEqual(putTag, getTag);
        Assert.AreEqual(1, h.EventMetricsTable.Invocations.Count(i => i.Method.Name == nameof(TableClient.AddEntityAsync)));
        Assert.AreEqual(0, h.EventMetricsTable.Invocations.Count(i => i.Method.Name == nameof(TableClient.UpdateEntityAsync)));
    }

    [TestMethod]
    public async Task Put_WithoutIfMatch_WhenARowIsStored_Is409_AndLeavesTheRowUnchanged()
    {
        var (id, _) = await CreateEvent();
        // Two admins both opened the empty metrics; the first saves.
        var (_, first) = await Put(id, Metrics(10, 2, 8, 50m));

        var result = await h.EventMetricsApi.PutEventMetrics(Json(Metrics(attendees: 11)), id, default);

        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(result));
        Assert.AreEqual(RouteFailures.ConflictMessage, ApiResults.Message(result));
        var (read, getTag) = await Get(id);
        AssertValues(read, 10, 2, 8, 50m);
        Assert.AreEqual(first, getTag);
        Assert.AreEqual(0, h.EventMetricsTable.Invocations.Count(i => i.Method.Name == nameof(TableClient.UpdateEntityAsync)),
            "no If-Match never replaces a stored row");
    }

    [TestMethod]
    public async Task Put_TwoConcurrentFirstSaves_OneWins_TheOtherIs409()
    {
        var (id, _) = await CreateEvent();
        var requests = new[] { Json(Metrics(10)), Json(Metrics(20)) };

        var results = await Task.WhenAll(requests.Select(r => h.EventMetricsApi.PutEventMetrics(r, id, default)));

        var statuses = results.Select(ApiResults.Status).ToArray();
        Assert.AreEqual(1, statuses.Count(s => s == StatusCodes.Status200OK));
        Assert.AreEqual(1, statuses.Count(s => s == StatusCodes.Status409Conflict));
        var winner = ApiResults.Value<EventMetricsEntity>(results.Single(r => ApiResults.Status(r) == StatusCodes.Status200OK));
        var (read, getTag) = await Get(id);
        Assert.AreEqual(winner.Attendees, read.Attendees);
        Assert.AreEqual(winner.ETag.ToString(), getTag);
    }

    [TestMethod]
    public async Task Put_WithTheCurrentIfMatch_Replaces_AndAnswersWithANewETag()
    {
        var (id, _) = await CreateEvent();
        var (_, first) = await Put(id, Metrics(10));

        var (_, second) = await Put(id, Metrics(12), first);

        Assert.AreNotEqual(first, second);
        Assert.AreEqual(12, (await Get(id)).Metrics.Attendees);
        var update = h.EventMetricsTable.Invocations.Single(i => i.Method.Name == nameof(TableClient.UpdateEntityAsync));
        Assert.AreEqual(new ETag(first), (ETag)update.Arguments[1]!);
    }

    [TestMethod]
    public async Task Put_IgnoresAnIdAndAnETagInTheBody()
    {
        var (id, _) = await CreateEvent();
        var (_, current) = await Put(id, Metrics(10));
        var other = Guid.NewGuid();

        // The header carries the current tag; the body's stale eTag and other id must not count.
        var request = RawJson($"{{\"id\":\"{other}\",\"eTag\":\"W/\\\"datetime'2000-01-01T00%3A00%3A00Z'\\\"\",\"attendees\":15}}", current);
        var result = await h.EventMetricsApi.PutEventMetrics(request, id, default);

        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(result), "a stale eTag in the body is not an If-Match");
        Assert.AreEqual(id, ApiResults.Value<EventMetricsEntity>(result).Id);
        Assert.AreEqual(15, (await Get(id)).Metrics.Attendees);
        Assert.ContainsSingle(h.EventMetrics);
        Assert.IsFalse(h.EventMetrics.ContainsKey(other.ToString("D")));
        Assert.AreNotEqual(current, ETagHeader(request));
    }

    [TestMethod]
    public async Task Put_ThatLosesAConcurrentCreate_Is409_AndKeepsTheWinner()
    {
        var (id, _) = await CreateEvent();
        var key = id.ToString("D");
        // The other create lands just before this PUT's insert. TableStore flattens the metrics
        // to a TableEntity (decimal is stored as a JSON string), so that is the insert it makes.
        h.EventMetricsTable.Setup(t => t.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>()))
            .Callback((TableEntity row, CancellationToken _) =>
                h.EventMetrics[row.RowKey] = new TableEntity("Metrics", row.RowKey) { ["Attendees"] = 99, ETag = TableMocks.NextETag() })
            .ThrowsAsync(new RequestFailedException(409, "Entity already exists", "EntityAlreadyExists", null));

        var result = await h.EventMetricsApi.PutEventMetrics(Json(Metrics(10)), id, default);

        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(result));
        Assert.AreEqual(RouteFailures.ConflictMessage, ApiResults.Message(result));
        Assert.AreEqual(99, h.EventMetrics[key].GetInt32("Attendees"));
    }
}
