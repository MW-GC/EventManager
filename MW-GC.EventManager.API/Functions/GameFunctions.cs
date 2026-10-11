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
    private readonly CrudHandler<GameEntity> _crud;

    public GameFunctions(TableStore<GameEntity> store, TableStore<ActivityEntity> activities, ILogger<GameFunctions> logger)
    {
        // A missing Game stays an idempotent 204. A Game that Activities still use is refused
        // (409) and nothing is deleted, so no Activity is left pointing at a Game that is gone.
        _crud = new CrudHandler<GameEntity>(store, logger, "games", CrudHandler<GameEntity>.Sync(GameValidator.Validate),
            async (id, ct) => (await activities.GetAllAsync(ct)).Count(a => a.GameId == id) is var inUse and > 0
                ? InUseMessage(inUse)
                : null);
    }

    [Function("GetGames")]
    public Task<IActionResult> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "games")] HttpRequest req,
        CancellationToken ct) => _crud.GetAllAsync("GetGames", ct);

    [Function("GetGame")]
    public Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "games/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct) => _crud.GetAsync(req, id, "GetGame", ct);

    [Function("CreateGame")]
    public Task<IActionResult> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "games")] HttpRequest req,
        CancellationToken ct) => _crud.CreateAsync(req, "CreateGame", ct);

    [Function("UpdateGame")]
    public Task<IActionResult> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "games/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct) => _crud.UpdateAsync(req, id, "UpdateGame", ct);

    [Function("DeleteGame")]
    public Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "games/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct) => _crud.DeleteAsync(req, id, "DeleteGame", ct);

    internal static string InUseMessage(int activities) => activities == 1
        ? "This game is used by 1 activity. Delete or move that activity first."
        : $"This game is used by {activities} activities. Delete or move them first.";
}
