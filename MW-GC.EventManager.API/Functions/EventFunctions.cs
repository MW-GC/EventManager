using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Shared.Requests;

namespace MW_GC.EventManager.API.Functions;

internal sealed class EventFunctions
{
    private readonly TableStore<EventEntity> _store;
    private readonly TableStore<GameEntity> _games;
    private readonly TableStore<ActivityEntity> _activities;
    private readonly EventGenerator _generator;
    private readonly ILogger<EventFunctions> _logger;

    public EventFunctions(
        TableStore<EventEntity> store,
        TableStore<GameEntity> games,
        TableStore<ActivityEntity> activities,
        EventGenerator generator,
        ILogger<EventFunctions> logger)
    {
        _store = store;
        _games = games;
        _activities = activities;
        _generator = generator;
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
        return evt is null ? new NotFoundResult() : new OkObjectResult(evt);
    });

    [Function("GenerateEvent")]
    public Task<IActionResult> Generate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "events/generate")] HttpRequest req,
        CancellationToken ct) => RouteFailures.RunAsync(_logger, "GenerateEvent", ct, async () =>
    {
        var body = await RequestBody.ReadAsync<GenerateEventRequest>(req, ct);
        if (!body.Ok) return body.Error;
        var request = body.Value;

        if (request.Count is < 1 or > EventEntity.MaximumSelections)
            return new BadRequestObjectResult($"Count must be between 1 and {EventEntity.MaximumSelections}.");
        if (request.SelectedGameIds is null || request.SelectedThemeIds is null || request.SelectedHolidayIds is null)
            return new BadRequestObjectResult("Filter lists must not be null.");
        if (request.UtcOffsetMinutes is < -GenerateEventRequest.MaximumUtcOffsetMinutes or > GenerateEventRequest.MaximumUtcOffsetMinutes)
            return new BadRequestObjectResult($"UtcOffsetMinutes must be between -{GenerateEventRequest.MaximumUtcOffsetMinutes} and {GenerateEventRequest.MaximumUtcOffsetMinutes}.");

        var gameEntities = await _games.GetAllAsync(ct);
        var activityEntities = await _activities.GetAllAsync(ct);

        var games = gameEntities.Select(g => new Game
        {
            Id = g.Id, Name = g.Name, ImageUrl = g.ImageUrl, Website = g.Website, IconUrl = g.IconUrl
        }).ToList();

        var activities = activityEntities.Select(a => new Activity
        {
            Id = a.Id, GameId = a.GameId, Name = a.Name, Description = a.Description,
            Rules = a.Rules, ThemeIds = a.ThemeIds, HolidayIds = a.HolidayIds,
            SetupRequirements = a.SetupRequirements, Comments = a.Comments
        }).ToList();

        var selections = _generator.Generate(games, activities, request);
        if (selections is null)
            return new BadRequestObjectResult("Not enough games/activities to satisfy the request.");

        // One clock read names the Event in the caller's local time and stores the UTC instant.
        var now = _generator.UtcNow;
        var entity = new EventEntity
        {
            Id = Guid.NewGuid(),
            Name = _generator.GenerateName(now, request.UtcOffsetMinutes),
            Date = now,
            Selections = selections,
            UniqueGamesOnly = request.UniqueGamesOnly
        };

        entity.NormalizeWinner();
        await _store.UpsertAsync(entity, ct);
        return new CreatedResult($"/api/events/{entity.Id}", entity);
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
                return new OkObjectResult(existing);
            return new ConflictObjectResult("This create key already exists with different details. Reload the event list and edit the saved event; do not start another create to retry this save.");
        }
        return new CreatedResult($"/api/events/{entity.Id}", entity);
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
        await _store.UpsertAsync(entity, ct);
        return new OkObjectResult(entity);
    });

    [Function("DeleteEvent")]
    public Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "events/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct) => RouteFailures.RunAsync(_logger, "DeleteEvent", ct, async () =>
    {
        await _store.DeleteAsync(id, ct);
        return new NoContentResult();
    });

    [Function("SelectWinner")]
    public Task<IActionResult> SelectWinner(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "events/{id:guid}/winner")] HttpRequest req,
        Guid id, CancellationToken ct) => RouteFailures.RunAsync(_logger, "SelectWinner", ct, async () =>
    {
        var entity = await _store.GetAsync(id, ct);
        if (entity is null) return new NotFoundResult();

        if (entity.Selections.Count == 0)
            return new BadRequestObjectResult("Event has no selections.");

        var winner = _generator.PickWinner(entity.Selections);
        entity.WinnerActivityId = winner.Activity.Id;
        await _store.UpsertAsync(entity, ct);
        return new OkObjectResult(entity);
    });
}
