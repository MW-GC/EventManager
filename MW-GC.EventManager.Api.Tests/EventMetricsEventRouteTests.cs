using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>Event metrics and the Event's own routes (#134): an Event PUT leaves them alone, an Event delete deletes them.</summary>
[TestClass]
public sealed class EventMetricsEventRouteTests : EventMetricsTestBase
{
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
