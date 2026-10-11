using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// Event metrics (#134, ADR 0001): their own row in the <c>EventMetrics</c> table keyed by the Event
/// id, read and written through <c>GET</c>/<c>PUT events/{id}/metrics</c>, through the real Functions
/// classes and <see cref="API.Services.TableStore{TEntity}"/> over in-memory tables.
/// </summary>
[TestClass]
public sealed class EventMetricsTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly LibraryHarness h = new();
    private readonly GameEntity game;

    public EventMetricsTests() => game = h.SeedGame("Owner");

    // ---- helpers

    private object EventBody(string name) => new
    {
        name, date = "2026-10-02T18:00:00Z", uniqueGamesOnly = true,
        selections = new[] { new { game = new { id = game.Id, name = "Owner" }, activity = new { id = Guid.NewGuid(), gameId = game.Id, name = "Act" } } },
    };

    private async Task<(Guid Id, string ETag)> CreateEvent(string name = "Game night")
    {
        var request = TestRequests.Json(EventBody(name));
        var result = await h.EventApi.SaveCustomized(request, default);
        Assert.AreEqual(StatusCodes.Status201Created, ApiResults.Status(result));
        return (ApiResults.Value<EventEntity>(result).Id, ETagHeader(request)!);
    }

    private static HttpRequest Json(object body, string? ifMatch = null)
    {
        var request = TestRequests.Json(body);
        if (ifMatch is not null) request.Headers["If-Match"] = ifMatch;
        return request;
    }

    private static HttpRequest RawJson(string body, string? ifMatch = null)
    {
        var request = TestRequests.Raw(body);
        if (ifMatch is not null) request.Headers["If-Match"] = ifMatch;
        return request;
    }

    private static HttpRequest Bare(string? ifMatch = null)
    {
        var request = TestRequests.Empty();
        if (ifMatch is not null) request.Headers["If-Match"] = ifMatch;
        return request;
    }

    private static string? ETagHeader(HttpRequest request) =>
        request.HttpContext.Response.Headers.ETag.Count == 0 ? null : request.HttpContext.Response.Headers.ETag.ToString();

    private static object Metrics(int? attendees = null, int? newAttendees = null, int? returning = null, decimal? amount = null) =>
        new { attendees, newAttendees, returningAttendees = returning, amountSpent = amount };

    /// <summary>A PUT that must succeed; returns the stored metrics and the ETag header.</summary>
    private async Task<(EventMetricsEntity Metrics, string ETag)> Put(Guid id, object body, string? ifMatch = null)
    {
        var request = Json(body, ifMatch);
        var result = await h.EventMetricsApi.PutEventMetrics(request, id, default);
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(result));
        var etag = ETagHeader(request);
        Assert.IsNotNull(etag, "PUT must answer with the row's ETag header.");
        var value = ApiResults.Value<EventMetricsEntity>(result);
        Assert.AreEqual(etag, value.ETag.ToString());
        return (value, etag);
    }

    private async Task<(EventMetricsEntity Metrics, string? ETag)> Get(Guid id)
    {
        var request = Bare();
        var result = await h.EventMetricsApi.GetEventMetrics(request, id, default);
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(result));
        return (ApiResults.Value<EventMetricsEntity>(result), ETagHeader(request));
    }

    private static void AssertValues(EventMetricsEntity actual, int? attendees, int? newAttendees, int? returning, decimal? amount)
    {
        Assert.AreEqual(attendees, actual.Attendees);
        Assert.AreEqual(newAttendees, actual.NewAttendees);
        Assert.AreEqual(returning, actual.ReturningAttendees);
        Assert.AreEqual(amount, actual.AmountSpent);
    }

    private int MetricsWrites() => h.EventMetricsTable.Invocations.Count(i => i.Method.Name is nameof(TableClient.UpsertEntityAsync)
        or nameof(TableClient.AddEntityAsync) or nameof(TableClient.UpdateEntityAsync));

    // ---- read

    [TestMethod]
    public async Task Get_WithNoStoredRow_Is200_WithEveryFieldNull_TheEventId_AndNoETag()
    {
        var (id, _) = await CreateEvent();

        var (metrics, etag) = await Get(id);

        Assert.AreEqual(id, metrics.Id);
        AssertValues(metrics, null, null, null, null);
        Assert.IsNull(etag, "an Event without a metrics row has no ETag to send back");
        Assert.AreEqual(default(ETag), metrics.ETag);
        Assert.IsEmpty(h.EventMetrics, "a read creates nothing");
    }

    [TestMethod]
    public async Task Put_ThenGet_ReturnsTheValues_WithTheStoredETag()
    {
        var (id, _) = await CreateEvent();

        var (written, putTag) = await Put(id, Metrics(30, 5, 25, 120.50m));
        var (read, getTag) = await Get(id);

        Assert.AreEqual(id, written.Id);
        AssertValues(read, 30, 5, 25, 120.50m);
        Assert.AreEqual(id, read.Id);
        Assert.AreEqual(putTag, getTag);
        Assert.AreEqual(putTag, read.ETag.ToString());
        Assert.IsTrue(h.EventMetrics.ContainsKey(id.ToString("D")), "the row key is the Event id");
        Assert.AreEqual("Metrics", h.EventMetrics[id.ToString("D")].PartitionKey);
    }

    [TestMethod]
    public async Task Put_WithEveryFieldNull_IsStored_AndClearsTheValues()
    {
        var (id, _) = await CreateEvent();
        var (_, first) = await Put(id, Metrics(30, 5, 25, 120.50m));

        var (cleared, second) = await Put(id, Metrics(), first);
        var (read, getTag) = await Get(id);

        AssertValues(cleared, null, null, null, null);
        AssertValues(read, null, null, null, null);
        Assert.AreNotEqual(first, second);
        Assert.AreEqual(second, getTag, "a cleared row is still a stored row with its own ETag");
    }

    // ---- validation

    [TestMethod]
    [DataRow("attendees", "0")]
    [DataRow("attendees", "1000000")]
    [DataRow("newAttendees", "0")]
    [DataRow("newAttendees", "1000000")]
    [DataRow("returningAttendees", "0")]
    [DataRow("returningAttendees", "1000000")]
    [DataRow("amountSpent", "0")]
    [DataRow("amountSpent", "1000000000")]
    public async Task Put_AtEachLimit_IsAccepted(string field, string value)
    {
        var (id, _) = await CreateEvent();

        var result = await h.EventMetricsApi.PutEventMetrics(RawJson($"{{\"{field}\":{value}}}"), id, default);

        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(result));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ApiResults.Value<EventMetricsEntity>(result), Web));
        Assert.AreEqual(decimal.Parse(value), json.RootElement.GetProperty(field).GetDecimal());
        Assert.AreEqual(1, MetricsWrites());
    }

    [TestMethod]
    [DataRow("attendees", "-1", "Attendees must be a whole number from 0 to 1000000.")]
    [DataRow("attendees", "1000001", "Attendees must be a whole number from 0 to 1000000.")]
    [DataRow("newAttendees", "-1", "New attendees must be a whole number from 0 to 1000000.")]
    [DataRow("newAttendees", "1000001", "New attendees must be a whole number from 0 to 1000000.")]
    [DataRow("returningAttendees", "-1", "Returning attendees must be a whole number from 0 to 1000000.")]
    [DataRow("returningAttendees", "1000001", "Returning attendees must be a whole number from 0 to 1000000.")]
    [DataRow("amountSpent", "-1", "Amount spent must be between 0 and 1000000000 with at most 2 decimal places.")]
    [DataRow("amountSpent", "1000000001", "Amount spent must be between 0 and 1000000000 with at most 2 decimal places.")]
    [DataRow("amountSpent", "1000000000.01", "Amount spent must be between 0 and 1000000000 with at most 2 decimal places.")]
    [DataRow("amountSpent", "12.345", "Amount spent must be between 0 and 1000000000 with at most 2 decimal places.")]
    public async Task Put_PastALimit_Is400_NamingTheField_AndWritesNothing(string field, string value, string message)
    {
        var (id, _) = await CreateEvent();

        var result = await h.EventMetricsApi.PutEventMetrics(RawJson($"{{\"{field}\":{value}}}"), id, default);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(result));
        Assert.AreEqual(message, ApiResults.Message(result));
        Assert.AreEqual(0, MetricsWrites());
        Assert.IsEmpty(h.EventMetrics);
    }

    [TestMethod]
    public async Task Put_WithSeveralBadFields_ReportsTheFirstInFieldOrder()
    {
        var (id, _) = await CreateEvent();

        var all = await h.EventMetricsApi.PutEventMetrics(Json(Metrics(-1, -1, -1, -1m)), id, default);
        var lastThree = await h.EventMetricsApi.PutEventMetrics(Json(Metrics(1, -1, -1, -1m)), id, default);
        var lastTwo = await h.EventMetricsApi.PutEventMetrics(Json(Metrics(1, 1, -1, -1m)), id, default);

        Assert.AreEqual("Attendees must be a whole number from 0 to 1000000.", ApiResults.Message(all));
        Assert.AreEqual("New attendees must be a whole number from 0 to 1000000.", ApiResults.Message(lastThree));
        Assert.AreEqual("Returning attendees must be a whole number from 0 to 1000000.", ApiResults.Message(lastTwo));
        Assert.AreEqual(0, MetricsWrites());
    }

    [TestMethod]
    [DataRow("{\"attendees\":\"many\"}")]
    [DataRow("{\"attendees\":1.5}")]
    [DataRow("{\"amountSpent\":\"lots\"}")]
    public async Task Put_WithANonNumberOrAFractionalCount_IsTheMalformedBody400(string body)
    {
        var (id, _) = await CreateEvent();

        var result = await h.EventMetricsApi.PutEventMetrics(RawJson(body), id, default);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(result));
        Assert.AreEqual(RequestBody.InvalidJsonMessage, ApiResults.Message(result));
        Assert.AreEqual(0, MetricsWrites());
    }

    [TestMethod]
    public async Task AmountSpent_RoundTripsThroughStorage_AndNullStaysNull()
    {
        var (id, _) = await CreateEvent();
        var key = id.ToString("D");

        var (_, etag) = await Put(id, Metrics(amount: 12.34m));
        Assert.AreEqual("12.34", h.EventMetrics[key].GetString(nameof(EventMetricsEntity.AmountSpent)),
            "decimal is outside TableStore's native list, so it is stored as JSON in a string property");
        Assert.AreEqual(12.34m, (await Get(id)).Metrics.AmountSpent);

        await Put(id, Metrics(attendees: 3), etag);
        var read = (await Get(id)).Metrics;
        Assert.IsNull(read.AmountSpent);
        Assert.AreEqual(3, read.Attendees);
    }

    // ---- unknown Event

    [TestMethod]
    public async Task UnknownEvent_Is404_OnGetAndPut_AndWritesNothing()
    {
        var unknown = Guid.NewGuid();

        Assert.IsExactInstanceOfType<NotFoundResult>(await h.EventMetricsApi.GetEventMetrics(Bare(), unknown, default));
        Assert.IsExactInstanceOfType<NotFoundResult>(await h.EventMetricsApi.PutEventMetrics(Json(Metrics(10)), unknown, default));
        Assert.IsExactInstanceOfType<NotFoundResult>(await h.EventMetricsApi.PutEventMetrics(Json(Metrics(-1)), unknown, default));
        Assert.AreEqual(0, MetricsWrites());
        Assert.IsEmpty(h.EventMetrics);
    }

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
        Assert.AreEqual(0, MetricsWrites());
    }

    [TestMethod]
    public async Task Put_WithoutIfMatch_ReplacesAStoredRow()
    {
        var (id, _) = await CreateEvent();
        var (_, first) = await Put(id, Metrics(10, 2, 8, 50m));

        var (replaced, second) = await Put(id, Metrics(attendees: 11));

        AssertValues(replaced, 11, null, null, null);
        AssertValues((await Get(id)).Metrics, 11, null, null, null);
        Assert.AreNotEqual(first, second);
        var update = h.EventMetricsTable.Invocations.Single(i => i.Method.Name == nameof(TableClient.UpdateEntityAsync));
        Assert.AreEqual(ETag.All, (ETag)update.Arguments[1]!, "no If-Match is an unconditional replace");
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

        var request = RawJson($"{{\"id\":\"{other}\",\"eTag\":\"W/\\\"datetime'2000-01-01T00%3A00%3A00Z'\\\"\",\"attendees\":15}}");
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
        // The other create lands between this PUT's read (no row yet) and its insert.
        h.EventMetricsTable.Setup(t => t.GetEntityAsync<TableEntity>(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string rowKey, IEnumerable<string> _, CancellationToken _) =>
            {
                h.EventMetrics[rowKey] = new TableEntity("Metrics", rowKey) { ["Attendees"] = 99, ETag = TableMocks.NextETag() };
                throw new RequestFailedException(404, "Missing");
            });

        var result = await h.EventMetricsApi.PutEventMetrics(Json(Metrics(10)), id, default);

        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(result));
        Assert.AreEqual(RouteFailures.ConflictMessage, ApiResults.Message(result));
        Assert.AreEqual(99, h.EventMetrics[key].GetInt32("Attendees"));
    }

    // ---- the Event's own routes

    [TestMethod]
    public async Task EventPut_LeavesStoredMetricsUntouched()
    {
        var (id, eventTag) = await CreateEvent("Before");
        var (_, metricsTag) = await Put(id, Metrics(30, 5, 25, 120.50m));

        var put = await h.EventApi.Update(Json(EventBody("After"), eventTag), id, default);

        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(put));
        Assert.AreEqual("After", h.Events[id.ToString("D")].GetString("Name"));
        var (read, readTag) = await Get(id);
        AssertValues(read, 30, 5, 25, 120.50m);
        Assert.AreEqual(metricsTag, readTag, "the metrics row was not rewritten");
        Assert.AreEqual(1, MetricsWrites());
    }

    [TestMethod]
    public async Task EventDelete_DeletesItsMetricsRow()
    {
        var (id, _) = await CreateEvent();
        var (other, _) = await CreateEvent("Other night");
        await Put(id, Metrics(10));
        await Put(other, Metrics(20));

        Assert.IsExactInstanceOfType<NoContentResult>(await h.EventApi.Delete(Bare(), id, default));

        Assert.IsFalse(h.EventMetrics.ContainsKey(id.ToString("D")));
        Assert.IsTrue(h.EventMetrics.ContainsKey(other.ToString("D")), "only the deleted Event's metrics go");
        Assert.IsExactInstanceOfType<NotFoundResult>(await h.EventMetricsApi.GetEventMetrics(Bare(), id, default));
    }

    [TestMethod]
    public async Task EventDelete_RefusedWith409_KeepsTheMetricsRow()
    {
        var (id, original) = await CreateEvent();
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(await h.EventApi.Update(Json(EventBody("Newer"), original), id, default)));
        await Put(id, Metrics(10));

        var result = await h.EventApi.Delete(Bare(original), id, default);

        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(result));
        Assert.IsTrue(h.Events.ContainsKey(id.ToString("D")));
        Assert.AreEqual(10, (await Get(id)).Metrics.Attendees);
        Assert.AreEqual(0, h.EventMetricsTable.Invocations.Count(i => i.Method.Name == nameof(TableClient.DeleteEntityAsync)));
    }

    [TestMethod]
    public async Task EventDelete_OfAnEventAlreadyGone_StillDeletesALeftoverMetricsRow()
    {
        var (id, _) = await CreateEvent();
        await Put(id, Metrics(10));
        // The first delete removed the Event, then failed before its metrics row went.
        h.Events.TryRemove(id.ToString("D"), out _);

        Assert.IsExactInstanceOfType<NoContentResult>(await h.EventApi.Delete(Bare(), id, default));

        Assert.IsEmpty(h.EventMetrics, "a retried delete cleans up");
    }
}
