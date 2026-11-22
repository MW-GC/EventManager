using System.Net;
using System.Text;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Tests;

// #177 items 3 and 6: the list read of every Event's metrics returns a result, like the single read and the
// save, and never throws for an API failure. It is the only list read of the metrics; the throwing one is gone.
[TestClass]
public sealed class EventMetricsClientTests
{
    private const string Path = "/api/event-metrics";
    private readonly StubApiHandler api = new();

    private EventMetricsClient Client() => new(api.Client());

    [TestMethod]
    public async Task LoadAll_ReturnsTheItems()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        api.On(HttpMethod.Get, Path, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"[{{\"eventId\":\"{first}\",\"attendees\":12,\"newAttendees\":4,\"returningAttendees\":8,\"amountSpent\":30.5,\"likes\":1}}," +
                $"{{\"eventId\":\"{second}\",\"attendees\":null,\"newAttendees\":null,\"returningAttendees\":7,\"amountSpent\":null,\"likes\":4}}]",
                Encoding.UTF8, "application/json")
        });

        var result = await Client().LoadAllAsync();

        var loaded = Assert.IsInstanceOfType<EventMetricsListResult.Loaded>(result);
        CollectionAssert.AreEqual(new[]
        {
            new EventMetricsWithLikes(first, 12, 4, 8, 30.5m, 1),
            new EventMetricsWithLikes(second, null, null, 7, null, 4)
        }, loaded.Items);
        Assert.AreEqual(1, api.Count(HttpMethod.Get, Path));
    }

    [TestMethod]
    public async Task LoadAll_AServerError_IsAFailureWithItsMessage()
    {
        api.Status(HttpMethod.Get, Path, HttpStatusCode.ServiceUnavailable, "Storage is temporarily unavailable. Try again shortly.");

        var result = await Client().LoadAllAsync();

        Assert.AreEqual("The server had a problem (HTTP 503). Try again.", Assert.IsInstanceOfType<EventMetricsListResult.Failed>(result).Error);
    }

    [TestMethod]
    public async Task LoadAll_NoConnection_IsAFailure()
    {
        api.Offline(HttpMethod.Get, Path);

        var result = await Client().LoadAllAsync();

        Assert.AreEqual(ApiResult.UnreachableMessage, Assert.IsInstanceOfType<EventMetricsListResult.Failed>(result).Error);
    }

    [TestMethod]
    [DataRow("not json")]
    [DataRow("null")]
    public async Task LoadAll_AnUnreadableBody_IsAFailure(string body)
    {
        api.On(HttpMethod.Get, Path, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

        var result = await Client().LoadAllAsync();

        Assert.AreEqual(ApiResult.UnreadableMessage, Assert.IsInstanceOfType<EventMetricsListResult.Failed>(result).Error);
    }
}
