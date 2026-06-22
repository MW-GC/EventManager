using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using MW_GC.EventManager.API.Services;
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

    public EventFunctions(
        TableStore<EventEntity> store,
        TableStore<GameEntity> games,
        TableStore<ActivityEntity> activities,
        EventGenerator generator)
    {
        _store = store;
        _games = games;
        _activities = activities;
        _generator = generator;
    }

    [Function("GetEvents")]
    public async Task<IActionResult> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "events")] HttpRequest req,
        CancellationToken ct)
    {
        var events = await _store.GetAllAsync(ct);
        return new OkObjectResult(events);
    }

    [Function("GetEvent")]
    public async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "events/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct)
    {
        var evt = await _store.GetAsync(id, ct);
        return evt is null ? new NotFoundResult() : new OkObjectResult(evt);
    }

    [Function("GenerateEvent")]
    public async Task<IActionResult> Generate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "events/generate")] HttpRequest req,
        CancellationToken ct)
    {
        var request = await req.ReadFromJsonAsync<GenerateEventRequest>(ct) ?? new();

        if (request.Count < 1)
            return new BadRequestObjectResult("Count must be at least 1.");

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

        var entity = new EventEntity
        {
            Id = Guid.NewGuid(),
            Name = EventGenerator.GenerateName(),
            Date = DateTimeOffset.UtcNow,
            Selections = selections,
            UniqueGamesOnly = request.UniqueGamesOnly
        };

        await _store.UpsertAsync(entity, ct);
        return new CreatedResult($"/api/events/{entity.Id}", entity);
    }

    [Function("SaveCustomizedEvent")]
    public async Task<IActionResult> SaveCustomized(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "events")] HttpRequest req,
        CancellationToken ct)
    {
        var entity = await req.ReadFromJsonAsync<EventEntity>(ct);
        if (entity is null) return new BadRequestResult();

        entity.Id = Guid.NewGuid();
        await _store.UpsertAsync(entity, ct);
        return new CreatedResult($"/api/events/{entity.Id}", entity);
    }

    [Function("UpdateEvent")]
    public async Task<IActionResult> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "events/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct)
    {
        var existing = await _store.GetAsync(id, ct);
        if (existing is null) return new NotFoundResult();

        var entity = await req.ReadFromJsonAsync<EventEntity>(ct);
        if (entity is null) return new BadRequestResult();

        entity.Id = id;
        await _store.UpsertAsync(entity, ct);
        return new OkObjectResult(entity);
    }

    [Function("DeleteEvent")]
    public async Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "events/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct)
    {
        await _store.DeleteAsync(id, ct);
        return new NoContentResult();
    }

    [Function("SelectWinner")]
    public async Task<IActionResult> SelectWinner(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "events/{id:guid}/winner")] HttpRequest req,
        Guid id, CancellationToken ct)
    {
        var entity = await _store.GetAsync(id, ct);
        if (entity is null) return new NotFoundResult();

        if (entity.Selections.Count == 0)
            return new BadRequestObjectResult("Event has no selections.");

        var winner = entity.Selections[Random.Shared.Next(entity.Selections.Count)];
        entity.WinnerActivityId = winner.Activity.Id;
        await _store.UpsertAsync(entity, ct);
        return new OkObjectResult(entity);
    }
}
