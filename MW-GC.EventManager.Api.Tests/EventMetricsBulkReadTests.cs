using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Models;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>The bulk read of every Event's metrics and Like count, <c>GET event-metrics</c> (#136).</summary>
[TestClass]
public sealed class EventMetricsBulkReadTests : EventMetricsTestBase
{
    [TestMethod]
    public async Task GetAll_WithNoMetricsAndNoLikes_IsAnEmptyArray()
    {
        await CreateEvent();

        Assert.IsEmpty(await GetAllMetrics());
    }

    [TestMethod]
    public async Task GetAll_AnEventWithMetricsOnly_HasItsValues_AndZeroLikes()
    {
        var (id, _) = await CreateEvent();
        await Put(id, Metrics(30, 5, 25, 120.50m));

        var item = Assert.ContainsSingle(await GetAllMetrics());

        Assert.AreEqual(new EventMetricsWithLikes(id, 30, 5, 25, 120.50m, 0), item);
    }

    [TestMethod]
    public async Task GetAll_AnEventWithLikesOnly_HasNullMetrics_AndItsLikeCount()
    {
        var (id, _) = await CreateEvent();
        SeedLikes(id, 2);

        var item = Assert.ContainsSingle(await GetAllMetrics());

        Assert.AreEqual(new EventMetricsWithLikes(id, null, null, null, null, 2), item);
    }

    [TestMethod]
    public async Task GetAll_AnEventWithMetricsAndLikes_IsOneItem()
    {
        var (id, _) = await CreateEvent();
        await Put(id, Metrics(attendees: 10));
        SeedLikes(id, 3);

        var item = Assert.ContainsSingle(await GetAllMetrics());

        Assert.AreEqual(new EventMetricsWithLikes(id, 10, null, null, null, 3), item);
    }

    [TestMethod]
    public async Task GetAll_TwoEvents_GetOneItemEach_AndAnEventWithNeitherGetsNone()
    {
        var (first, _) = await CreateEvent("First");
        var (second, _) = await CreateEvent("Second");
        var (neither, _) = await CreateEvent("Neither");
        await Put(first, Metrics(12, 4, 8, 30m));
        SeedLikes(first, 1);
        await Put(second, Metrics(returning: 7));
        SeedLikes(second, 4);

        var items = await GetAllMetrics();

        CollectionAssert.AreEquivalent(new[]
        {
            new EventMetricsWithLikes(first, 12, 4, 8, 30m, 1),
            new EventMetricsWithLikes(second, null, null, 7, null, 4),
        }, items, $"exactly these two items: none for {neither}");
    }

    [TestMethod]
    public async Task GetAll_SkipsALikesPartitionKeyThatIsNotAGuid()
    {
        var (id, _) = await CreateEvent();
        SeedLikes(id, 1);
        SeedLikes("not-an-event-id", 5);

        var item = Assert.ContainsSingle(await GetAllMetrics());

        Assert.AreEqual(new EventMetricsWithLikes(id, null, null, null, null, 1), item);
    }

    [TestMethod]
    [DataRow("B")]
    [DataRow("N")]
    public async Task GetAll_DoesNotCountALikesPartitionKeyInAnotherGuidForm(string format)
    {
        var (id, _) = await CreateEvent();
        SeedLikes(id, 1);
        // The same Event id in braces or without hyphens: Like rows are written in the "D" form only.
        SeedLikes(id.ToString(format), 3);

        var item = Assert.ContainsSingle(await GetAllMetrics());

        Assert.AreEqual(new EventMetricsWithLikes(id, null, null, null, null, 1), item);
    }

    [TestMethod]
    public async Task GetAll_CountsLikesInOneQueryOfTheWholeTable_SelectingOnlyTheKeys()
    {
        var (first, _) = await CreateEvent("First");
        var (second, _) = await CreateEvent("Second");
        SeedLikes(first, 2);
        SeedLikes(second, 1);

        var items = await GetAllMetrics();

        CollectionAssert.AreEquivalent(new[] { (first, 2), (second, 1) }, items.Select(i => (i.EventId, i.Likes)).ToArray());
        // One read of the Likes table, with no filter (every Event's Likes in one pass) and only the keys selected.
        h.LikesTable.Verify(t => t.QueryAsync<TableEntity>(
            (string?)null, It.IsAny<int?>(),
            It.Is<IEnumerable<string>>(select => select.Order().SequenceEqual(new[] { "PartitionKey", "RowKey" })),
            It.IsAny<CancellationToken>()), Times.Once());
        Assert.AreEqual(1, h.LikesTable.Invocations.Count(i => i.Method.Name == nameof(TableClient.QueryAsync)));
    }

    [TestMethod]
    [DataRow("metrics")]
    [DataRow("likes")]
    public async Task GetAll_AStorageOutage_IsTheMapped503_AndIsLogged(string table)
    {
        var outage = new RequestFailedException(503, "storage down");
        if (table == "metrics")
            h.EventMetricsTable.Setup(t => t.QueryAsync(It.IsAny<System.Linq.Expressions.Expression<Func<TableEntity, bool>>>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .Throws(outage);
        else
            h.LikesTable.Setup(t => t.QueryAsync<TableEntity>(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .Throws(outage);

        var result = await h.EventMetricsApi.GetAllEventMetrics(Bare(), default);

        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, ApiResults.Status(result));
        Assert.AreEqual(RouteFailures.UnavailableMessage, ApiResults.Message(result));
        var entry = Assert.ContainsSingle(h.EventMetricsLog.Entries);
        Assert.AreSame(outage, entry.Exception);
        Assert.Contains("GetAllEventMetrics", entry.Message);
    }

    [TestMethod]
    public async Task GetAll_Json_IsAnArrayOfCamelCaseItems()
    {
        var (id, _) = await CreateEvent();
        await Put(id, Metrics(30, 5, 25, 120.50m));
        SeedLikes(id, 2);

        var result = await h.EventMetricsApi.GetAllEventMetrics(Bare(), default);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsInstanceOfType<ObjectResult>(result).Value, Web));
        var item = Assert.ContainsSingle(json.RootElement.EnumerateArray().ToList());
        CollectionAssert.AreEqual(
            new[] { "eventId", "attendees", "newAttendees", "returningAttendees", "amountSpent", "likes" },
            item.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.AreEqual(id.ToString("D"), item.GetProperty("eventId").GetString());
        Assert.AreEqual(120.50m, item.GetProperty("amountSpent").GetDecimal());
        Assert.AreEqual(2, item.GetProperty("likes").GetInt32());
    }
}
