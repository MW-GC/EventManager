using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.API.Functions;

/// <summary>
/// An Event's Event metrics (#134, ADR 0001): one row per Event in the <c>EventMetrics</c> table,
/// keyed by the Event id, with its own ETag. Read and written only here; the Event's own routes
/// never touch it, except that deleting an Event deletes its row (<see cref="EventFunctions"/>).
/// </summary>
internal sealed class EventMetricsFunctions
{
    private readonly TableStore<EventMetricsEntity> _store;
    private readonly TableStore<EventEntity> _events;
    private readonly ILogger<EventMetricsFunctions> _logger;

    public EventMetricsFunctions(
        TableStore<EventMetricsEntity> store,
        TableStore<EventEntity> events,
        ILogger<EventMetricsFunctions> logger)
    {
        _store = store;
        _events = events;
        _logger = logger;
    }

    /// <summary>
    /// 404 for an unknown Event. An Event with no stored row reads as empty metrics: every field
    /// null, the Event id, and no ETag header (there is no row a tag could match).
    /// </summary>
    [Function("GetEventMetrics")]
    public Task<IActionResult> GetEventMetrics(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "events/{id:guid}/metrics")] HttpRequest req,
        Guid id, CancellationToken ct) => RouteFailures.RunAsync(_logger, "GetEventMetrics", ct, async () =>
    {
        if (await _events.GetAsync(id, ct) is null) return new NotFoundResult();
        var metrics = await _store.GetAsync(id, ct) ?? new EventMetricsEntity { Id = id };
        return EntityResults.Ok(req, metrics);
    });

    /// <summary>
    /// Replaces the Event's metrics with the body's fields (an all-null body clears them and is
    /// stored). An <c>id</c> or <c>eTag</c> in the body is ignored. With no stored row, no If-Match
    /// creates it and an If-Match is a 409 (a tag cannot match a missing row); with a stored row,
    /// no If-Match replaces it and a stale If-Match is a 409. A create that loses a race is a 409.
    /// Every success is a 200 with the row and its ETag.
    /// </summary>
    [Function("PutEventMetrics")]
    public Task<IActionResult> PutEventMetrics(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "events/{id:guid}/metrics")] HttpRequest req,
        Guid id, CancellationToken ct) => RouteFailures.RunAsync(_logger, "PutEventMetrics", ct, async () =>
    {
        if (await _events.GetAsync(id, ct) is null) return new NotFoundResult();

        var body = await RequestBody.ReadAsync<EventMetricsEntity>(req, ct);
        if (!body.Ok) return body.Error;
        var metrics = body.Value;
        if (EventMetricsValidator.Validate(metrics) is { } error) return new BadRequestObjectResult(error);

        metrics.Id = id;
        if (await _store.GetAsync(id, ct) is not null)
            return await EntityResults.UpdateAsync(_store, req, metrics, ct);

        if (EntityResults.IfMatch(req) != default) return EntityResults.Conflict();
        metrics.ETag = default;
        return await _store.TryAddAsync(metrics, ct) ? EntityResults.Ok(req, metrics) : EntityResults.Conflict();
    });
}
