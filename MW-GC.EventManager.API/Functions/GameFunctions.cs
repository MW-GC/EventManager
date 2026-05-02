using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.API.Functions;

internal sealed class GameFunctions
{
    private readonly TableStore<GameEntity> _store;

    public GameFunctions(TableStore<GameEntity> store) => _store = store;

    [Function("GetGames")]
    public async Task<IActionResult> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "games")] HttpRequest req,
        CancellationToken ct)
    {
        var games = await _store.GetAllAsync(ct);
        return new OkObjectResult(games);
    }

    [Function("GetGame")]
    public async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "games/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct)
    {
        var game = await _store.GetAsync(id, ct);
        return game is null ? new NotFoundResult() : new OkObjectResult(game);
    }

    [Function("CreateGame")]
    public async Task<IActionResult> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "games")] HttpRequest req,
        CancellationToken ct)
    {
        var entity = await req.ReadFromJsonAsync<GameEntity>(ct);
        if (entity is null) return new BadRequestResult();

        entity.Id = Guid.NewGuid();
        await _store.UpsertAsync(entity, ct);
        return new CreatedResult($"/api/games/{entity.Id}", entity);
    }

    [Function("UpdateGame")]
    public async Task<IActionResult> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "games/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct)
    {
        var existing = await _store.GetAsync(id, ct);
        if (existing is null) return new NotFoundResult();

        var entity = await req.ReadFromJsonAsync<GameEntity>(ct);
        if (entity is null) return new BadRequestResult();

        entity.Id = id;
        await _store.UpsertAsync(entity, ct);
        return new OkObjectResult(entity);
    }

    [Function("DeleteGame")]
    public async Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "games/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct)
    {
        await _store.DeleteAsync(id, ct);
        return new NoContentResult();
    }
}
