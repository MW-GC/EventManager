using System.Text.Json;
using Azure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// Event metrics (#134, ADR 0001): their own row in the <c>EventMetrics</c> table keyed by the Event
/// id, read and written through <c>GET</c>/<c>PUT events/{id}/metrics</c>, through the real Functions
/// classes and <see cref="API.Services.TableStore{TEntity}"/> over in-memory tables.
/// </summary>
[TestClass]
public sealed class EventMetricsTests : EventMetricsTestBase
{
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
}
