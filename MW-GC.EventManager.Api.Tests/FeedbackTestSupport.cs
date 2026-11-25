using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using MW_GC.EventManager.API.Functions;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// Shared set-up of the feedback tests (#101): in-memory Events, FeedbackLinks and FeedbackLikes tables
/// behind the real stores, a manual clock for the rate limit, and the request helpers.
/// </summary>
public abstract class FeedbackTestBase
{
    private protected readonly ConcurrentDictionary<string, TableEntity> _events = new();
    private protected readonly ConcurrentDictionary<string, TableEntity> _links = new();
    private protected readonly ConcurrentDictionary<string, TableEntity> _likes = new();
    private protected readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
    private protected readonly EventFunctions _eventApi;
    private protected readonly FeedbackFunctions _api;

    private protected FeedbackTestBase()
    {
        var service = new Mock<TableServiceClient>();
        service.Setup(s => s.GetTableClient("Events")).Returns(TableMocks.Table(_events).Object);
        service.Setup(s => s.GetTableClient(FeedbackStore.LinksTable)).Returns(TableMocks.Table(_links).Object);
        service.Setup(s => s.GetTableClient(FeedbackStore.LikesTable)).Returns(TableMocks.FilteringTable(_likes).Object);
        service.Setup(s => s.GetTableClient("EventMetrics")).Returns(TableMocks.Table(new ConcurrentDictionary<string, TableEntity>()).Object);
        var events = new TableStore<EventEntity>(service.Object, "Events", "Event");
        var metrics = new TableStore<EventMetricsEntity>(service.Object, "EventMetrics", "Metrics");
        service.Setup(s => s.GetTableClient(TableNames.Series)).Returns(TableMocks.Table(new ConcurrentDictionary<string, TableEntity>()).Object);
        var series = new TableStore<SeriesEntity>(service.Object, TableNames.Series, TableNames.SeriesPartition);
        _eventApi = new EventFunctions(events, null!, null!, metrics, series, new ListLogger<EventFunctions>());
        _api = new FeedbackFunctions(new FeedbackStore(service.Object), events, new LikeRateLimiter(_time), new ListLogger<FeedbackFunctions>());
    }

    private protected static readonly DateTimeOffset EventDate = new(2026, 10, 9, 19, 0, 0, TimeSpan.Zero);

    private protected async Task<Guid> CreateEvent(string name = "Game night")
    {
        var game = new Game { Id = Guid.NewGuid(), Name = "Alpha" };
        var evt = new EventEntity
        {
            Name = name,
            Date = EventDate,
            Selections = [new Selection { Game = game, Activity = new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Race" } }],
        };
        var result = await _eventApi.SaveCustomized(TestRequests.Json(evt), default);
        Assert.AreEqual(201, ApiResults.Status(result));
        return ApiResults.Value<EventEntity>(result).Id;
    }

    private protected async Task<string> Link(Guid id) =>
        ApiResults.Value<FeedbackLink>(await _api.GetLink(TestRequests.Empty(), id, default)).Token;

    private protected async Task<int> Count(Guid id) =>
        ApiResults.Value<EventLikes>(await _api.GetLikes(TestRequests.Empty(), id, default)).Count;

    private protected static HttpRequest From(string forwardedFor)
    {
        var request = TestRequests.Empty();
        request.Headers[ClientIp.ForwardedForHeader] = forwardedFor;
        return request;
    }

    private protected static string Json(IActionResult result) =>
        JsonSerializer.Serialize(Assert.IsInstanceOfType<ObjectResult>(result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private protected sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
