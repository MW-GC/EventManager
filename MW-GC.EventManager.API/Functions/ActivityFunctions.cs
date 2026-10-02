using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.API.Functions;

internal sealed class ActivityFunctions
{
    private readonly TableStore<ActivityEntity> _store;
    private readonly TableStore<GameEntity> _games;
    private readonly ILogger<ActivityFunctions> _logger;

    public ActivityFunctions(TableStore<ActivityEntity> store, TableStore<GameEntity> games, ILogger<ActivityFunctions> logger)
    {
        _store = store;
        _games = games;
        _logger = logger;
    }

    [Function("GetActivities")]
    public Task<IActionResult> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "activities")] HttpRequest req,
        CancellationToken ct) => RouteFailures.RunAsync(_logger, "GetActivities", ct, async () =>
    {
        var activities = await _store.GetAllAsync(ct);
        return new OkObjectResult(activities);
    });

    [Function("GetActivity")]
    public Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "activities/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct) => RouteFailures.RunAsync(_logger, "GetActivity", ct, async () =>
    {
        var activity = await _store.GetAsync(id, ct);
        return activity is null ? new NotFoundResult() : new OkObjectResult(activity);
    });

    [Function("CreateActivity")]
    public Task<IActionResult> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "activities")] HttpRequest req,
        CancellationToken ct) => RouteFailures.RunAsync(_logger, "CreateActivity", ct, async () =>
    {
        var body = await RequestBody.ReadAsync<ActivityEntity>(req, ct);
        if (!body.Ok) return body.Error;
        var entity = body.Value;
        if (await ValidateAsync(entity, ct) is { } error) return new BadRequestObjectResult(error);

        entity.Id = Guid.NewGuid();
        await _store.UpsertAsync(entity, ct);
        return new CreatedResult($"/api/activities/{entity.Id}", entity);
    });

    [Function("UpdateActivity")]
    public Task<IActionResult> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "activities/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct) => RouteFailures.RunAsync(_logger, "UpdateActivity", ct, async () =>
    {
        var existing = await _store.GetAsync(id, ct);
        if (existing is null) return new NotFoundResult();

        var body = await RequestBody.ReadAsync<ActivityEntity>(req, ct);
        if (!body.Ok) return body.Error;
        var entity = body.Value;
        if (await ValidateAsync(entity, ct) is { } error) return new BadRequestObjectResult(error);

        entity.Id = id;
        await _store.UpsertAsync(entity, ct);
        return new OkObjectResult(entity);
    });

    // The shared rules, then the one rule that needs storage: the Game must exist.
    private async Task<string?> ValidateAsync(ActivityEntity entity, CancellationToken ct)
    {
        if (ActivityValidator.Validate(entity) is { } error) return error;
        return await _games.GetAsync(entity.GameId, ct) is null ? ActivityValidator.GameNotFoundMessage : null;
    }

    [Function("DeleteActivity")]
    public Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "activities/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct) => RouteFailures.RunAsync(_logger, "DeleteActivity", ct, async () =>
    {
        await _store.DeleteAsync(id, ct);
        return new NoContentResult();
    });

    [Function("DuplicateActivity")]
    public Task<IActionResult> Duplicate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "activities/{id:guid}/duplicate")] HttpRequest req,
        Guid id, CancellationToken ct) => RouteFailures.RunAsync(_logger, "DuplicateActivity", ct, async () =>
    {
        var source = await _store.GetAsync(id, ct);
        if (source is null) return new NotFoundResult();

        var duplicate = new ActivityEntity
        {
            Id = Guid.NewGuid(),
            GameId = source.GameId,
            Name = $"{source.Name} (Copy)",
            Description = source.Description,
            Rules = source.Rules,
            ThemeIds = [.. source.ThemeIds],
            HolidayIds = [.. source.HolidayIds],
            SetupRequirements = source.SetupRequirements,
            Comments = source.Comments
        };

        await _store.UpsertAsync(duplicate, ct);
        return new CreatedResult($"/api/activities/{duplicate.Id}", duplicate);
    });

    [Function("UpdateActivityComments")]
    public Task<IActionResult> UpdateComments(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "activities/{id:guid}/comments")] HttpRequest req,
        Guid id, CancellationToken ct) => RouteFailures.RunAsync(_logger, "UpdateActivityComments", ct, async () =>
    {
        var existing = await _store.GetAsync(id, ct);
        if (existing is null) return new NotFoundResult();

        var body = await RequestBody.ReadAsync<CommentsPayload>(req, ct);
        if (!body.Ok) return body.Error;
        if (ActivityValidator.ValidateComments(body.Value.Comments) is { } error) return new BadRequestObjectResult(error);

        existing.Comments = body.Value.Comments;
        await _store.UpsertAsync(existing, ct);
        return new OkObjectResult(existing);
    });

    private record CommentsPayload(string? Comments);
}
