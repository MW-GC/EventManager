using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.API.Functions;

internal sealed class GameFunctions
{
    private readonly TableStore<GameEntity> _store;
    private readonly ILogger<GameFunctions> _logger;

    public GameFunctions(TableStore<GameEntity> store, ILogger<GameFunctions> logger)
    {
        _store = store;
        _logger = logger;
    }

    [Function("GetGames")]
    public Task<IActionResult> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "games")] HttpRequest req,
        CancellationToken ct) => RouteFailures.RunAsync(_logger, "GetGames", ct, async () =>
    {
        var games = await _store.GetAllAsync(ct);
        return new OkObjectResult(games);
    });

    [Function("GetGame")]
    public Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "games/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct) => RouteFailures.RunAsync(_logger, "GetGame", ct, async () =>
    {
        var game = await _store.GetAsync(id, ct);
        return game is null ? new NotFoundResult() : new OkObjectResult(game);
    });

    [Function("CreateGame")]
    public Task<IActionResult> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "games")] HttpRequest req,
        CancellationToken ct) => RouteFailures.RunAsync(_logger, "CreateGame", ct, async () =>
    {
        var body = await RequestBody.ReadAsync<GameEntity>(req, ct);
        if (!body.Ok) return body.Error;
        var entity = body.Value;
        if (GameValidator.Validate(entity) is { } error) return new BadRequestObjectResult(error);

        entity.Id = Guid.NewGuid();
        await _store.UpsertAsync(entity, ct);
        return new CreatedResult($"/api/games/{entity.Id}", entity);
    });

    [Function("UpdateGame")]
    public Task<IActionResult> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "games/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct) => RouteFailures.RunAsync(_logger, "UpdateGame", ct, async () =>
    {
        var existing = await _store.GetAsync(id, ct);
        if (existing is null) return new NotFoundResult();

        var body = await RequestBody.ReadAsync<GameEntity>(req, ct);
        if (!body.Ok) return body.Error;
        var entity = body.Value;
        if (GameValidator.Validate(entity) is { } error) return new BadRequestObjectResult(error);

        entity.Id = id;
        await _store.UpsertAsync(entity, ct);
        return new OkObjectResult(entity);
    });

    [Function("DeleteGame")]
    public Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "games/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct) => RouteFailures.RunAsync(_logger, "DeleteGame", ct, async () =>
    {
        await _store.DeleteAsync(id, ct);
        return new NoContentResult();
    });
}
