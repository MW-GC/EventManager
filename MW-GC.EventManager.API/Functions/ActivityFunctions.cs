using Azure;
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
    private readonly CrudHandler<ActivityEntity> _crud;

    public ActivityFunctions(TableStore<ActivityEntity> store, TableStore<GameEntity> games, ILogger<ActivityFunctions> logger)
    {
        _store = store;
        _games = games;
        _logger = logger;
        // No delete guard: nothing refers to an Activity by id, so a delete goes straight to storage.
        _crud = new CrudHandler<ActivityEntity>(store, logger, "activities", ValidateAsync);
    }

    [Function("GetActivities")]
    public Task<IActionResult> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "activities")] HttpRequest req,
        CancellationToken ct) => _crud.GetAllAsync("GetActivities", ct);

    [Function("GetActivity")]
    public Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "activities/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct) => _crud.GetAsync(req, id, "GetActivity", ct);

    [Function("CreateActivity")]
    public Task<IActionResult> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "activities")] HttpRequest req,
        CancellationToken ct) => _crud.CreateAsync(req, "CreateActivity", ct);

    [Function("UpdateActivity")]
    public Task<IActionResult> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "activities/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct) => _crud.UpdateAsync(req, id, "UpdateActivity", ct);

    // The shared rules, then the one rule that needs storage: the Game must exist.
    private async Task<string?> ValidateAsync(ActivityEntity entity, CancellationToken ct)
    {
        if (ActivityValidator.Validate(entity) is { } error) return error;
        return await _games.GetAsync(entity.GameId, ct) is null ? ActivityValidator.GameNotFoundMessage : null;
    }

    [Function("DeleteActivity")]
    public Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "activities/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct) => _crud.DeleteAsync(req, id, "DeleteActivity", ct);

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
        return EntityResults.Created(req, $"/api/activities/{duplicate.Id}", duplicate);
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

        // Write back with the ETag just read (or the caller's own If-Match), so a write that lands
        // between the read and this write makes the PATCH a 409 instead of being overwritten.
        var ifMatch = EntityResults.IfMatch(req);
        if (ifMatch == default || ifMatch == ETag.All) ifMatch = existing.ETag;
        existing.Comments = body.Value.Comments;
        return await EntityResults.UpdateAsync(_store, req, existing, ct, ifMatch);
    });

    private record CommentsPayload(string? Comments);
}
