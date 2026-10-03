using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.API.Functions;

internal sealed class EventFunctions
{
    private readonly TableStore<EventEntity> _store;
    private readonly TableStore<GameEntity> _games;
    private readonly TableStore<ActivityEntity> _activities;
    private readonly ILogger<EventFunctions> _logger;

    public EventFunctions(
        TableStore<EventEntity> store,
        TableStore<GameEntity> games,
        TableStore<ActivityEntity> activities,
        ILogger<EventFunctions> logger)
    {
        _store = store;
        _games = games;
        _activities = activities;
        _logger = logger;
    }

    [Function("GetEvents")]
    public Task<IActionResult> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "events")] HttpRequest req,
        CancellationToken ct) => RouteFailures.RunAsync(_logger, "GetEvents", ct, async () =>
    {
        var events = await _store.GetAllAsync(ct);
        return new OkObjectResult(events);
    });

    [Function("GetEvent")]
    public Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "events/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct) => RouteFailures.RunAsync(_logger, "GetEvent", ct, async () =>
    {
        var evt = await _store.GetAsync(id, ct);
        return evt is null ? new NotFoundResult() : EntityResults.Ok(req, evt);
    });

    [Function("SaveCustomizedEvent")]
    public Task<IActionResult> SaveCustomized(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "events")] HttpRequest req,
        CancellationToken ct) => RouteFailures.RunAsync(_logger, "SaveCustomizedEvent", ct, async () =>
    {
        var body = await RequestBody.ReadAsync<EventEntity>(req, ct);
        if (!body.Ok) return body.Error;
        var entity = body.Value;
        if (EventValidator.Validate(entity) is { } error) return new BadRequestObjectResult(error);

        var hasKey = req.Headers.TryGetValue("Idempotency-Key", out var keys);
        var createId = Guid.NewGuid();
        if (hasKey && (keys.Count != 1 || !Guid.TryParseExact(keys[0], "D", out createId) || createId == Guid.Empty))
            return new BadRequestObjectResult("Idempotency-Key must be a non-empty GUID in D format.");

        entity.Id = createId;
        entity.NormalizeWinner();
        // Older clients still get a fresh ID; all creates use atomic inserts.
        if (!await _store.TryAddAsync(entity, ct))
        {
            var existing = await _store.GetAsync(createId, ct);
            if (existing is not null && CreateDetails(existing) == CreateDetails(entity))
                return EntityResults.Ok(req, existing);
            return new ConflictObjectResult("This create key already exists with different details. Reload the event list and edit the saved event; do not start another create to retry this save.");
        }
        return EntityResults.Created(req, $"/api/events/{entity.Id}", entity);
    });

    // Compare normalized domain data, not row keys, timestamps or ETags. Never overwrite
    // a later edit when reconciling an ambiguous create response.
    private static string CreateDetails(EventEntity entity) => System.Text.Json.JsonSerializer.Serialize(new
    {
        entity.Name, Date = entity.Date.ToUniversalTime(), entity.Selections, entity.UniqueGamesOnly, entity.WinnerActivityId
    });

    [Function("UpdateEvent")]
    public Task<IActionResult> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "events/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct) => RouteFailures.RunAsync(_logger, "UpdateEvent", ct, async () =>
    {
        var existing = await _store.GetAsync(id, ct);
        if (existing is null) return new NotFoundResult();

        var body = await RequestBody.ReadAsync<EventEntity>(req, ct);
        if (!body.Ok) return body.Error;
        var entity = body.Value;
        if (EventValidator.Validate(entity) is { } error) return new BadRequestObjectResult(error);

        entity.Id = id;
        entity.NormalizeWinner();
        return await EntityResults.UpdateAsync(_store, req, entity, ct);
    });

    [Function("DeleteEvent")]
    public Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "events/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct) => RouteFailures.RunAsync(_logger, "DeleteEvent", ct, async () =>
    {
        return await EntityResults.DeleteAsync(_store, req, id, ct);
    });
}
