using System.Text.Json;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// Shared set-up of the Event metrics tests (#134, ADR 0001): one in-memory library
/// (<see cref="LibraryHarness"/>) with a Game, and the request, read and Like-seeding helpers.
/// </summary>
public abstract class EventMetricsTestBase
{
    private protected static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private protected readonly LibraryHarness h = new();
    private protected readonly GameEntity game;

    private protected EventMetricsTestBase() => game = h.SeedGame("Owner");

    // ---- helpers

    private protected object EventBody(string name) => new
    {
        name, date = "2026-10-02T18:00:00Z", uniqueGamesOnly = true,
        selections = new[] { new { game = new { id = game.Id, name = "Owner" }, activity = new { id = Guid.NewGuid(), gameId = game.Id, name = "Act" } } },
    };

    private protected async Task<(Guid Id, string ETag)> CreateEvent(string name = "Game night")
    {
        var request = TestRequests.Json(EventBody(name));
        var result = await h.EventApi.SaveCustomized(request, default);
        Assert.AreEqual(StatusCodes.Status201Created, ApiResults.Status(result));
        return (ApiResults.Value<EventEntity>(result).Id, ETagHeader(request)!);
    }

    private protected static HttpRequest Json(object body, string? ifMatch = null)
    {
        var request = TestRequests.Json(body);
        if (ifMatch is not null) request.Headers["If-Match"] = ifMatch;
        return request;
    }

    private protected static HttpRequest RawJson(string body, string? ifMatch = null)
    {
        var request = TestRequests.Raw(body);
        if (ifMatch is not null) request.Headers["If-Match"] = ifMatch;
        return request;
    }

    private protected static HttpRequest Bare(string? ifMatch = null)
    {
        var request = TestRequests.Empty();
        if (ifMatch is not null) request.Headers["If-Match"] = ifMatch;
        return request;
    }

    private protected static string? ETagHeader(HttpRequest request) =>
        request.HttpContext.Response.Headers.ETag.Count == 0 ? null : request.HttpContext.Response.Headers.ETag.ToString();

    private protected static object Metrics(int? attendees = null, int? newAttendees = null, int? returning = null, decimal? amount = null) =>
        new { attendees, newAttendees, returningAttendees = returning, amountSpent = amount };

    /// <summary>A PUT that must succeed; returns the stored metrics and the ETag header.</summary>
    private protected async Task<(EventMetricsEntity Metrics, string ETag)> Put(Guid id, object body, string? ifMatch = null)
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

    private protected async Task<(EventMetricsEntity Metrics, string? ETag)> Get(Guid id)
    {
        var request = Bare();
        var result = await h.EventMetricsApi.GetEventMetrics(request, id, default);
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(result));
        return (ApiResults.Value<EventMetricsEntity>(result), ETagHeader(request));
    }

    private protected static void AssertValues(EventMetricsEntity actual, int? attendees, int? newAttendees, int? returning, decimal? amount)
    {
        Assert.AreEqual(attendees, actual.Attendees);
        Assert.AreEqual(newAttendees, actual.NewAttendees);
        Assert.AreEqual(returning, actual.ReturningAttendees);
        Assert.AreEqual(amount, actual.AmountSpent);
    }

    private protected int MetricsWrites() => h.EventMetricsTable.Invocations.Count(i => i.Method.Name is nameof(TableClient.UpsertEntityAsync)
        or nameof(TableClient.AddEntityAsync) or nameof(TableClient.UpdateEntityAsync));

    private protected void SeedLikes(string partitionKey, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var rowKey = Guid.NewGuid().ToString("N");
            h.Likes[rowKey] = new TableEntity(partitionKey, rowKey);
        }
    }

    private protected void SeedLikes(Guid eventId, int count) => SeedLikes(eventId.ToString("D"), count);

    private protected async Task<List<EventMetricsWithLikes>> GetAllMetrics()
    {
        var result = await h.EventMetricsApi.GetAllEventMetrics(Bare(), default);
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(result));
        return ApiResults.Value<List<EventMetricsWithLikes>>(result);
    }
}
