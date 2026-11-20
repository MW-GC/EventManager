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
    private readonly TableStore<CommentEntity> _comments;
    private readonly ILogger<ActivityFunctions> _logger;
    private readonly CrudHandler<ActivityEntity> _crud;

    public ActivityFunctions(
        TableStore<ActivityEntity> store,
        TableStore<GameEntity> games,
        TableStore<CommentEntity> comments,
        ActivityNoteConversion conversion,
        ILogger<ActivityFunctions> logger)
    {
        _store = store;
        _games = games;
        _comments = comments;
        _logger = logger;
        // No delete guard: nothing refers to an Activity by id, so a delete goes straight to storage.
        // A PUT replaces the row through the typed entity, which has no old Comments column, so the
        // old texts are converted into Comments first (#133); when that fails the PUT fails and
        // writes nothing. A create and a duplicate always write a new id, so they never replace a
        // row that holds an old text, and a delete removes the row and its old text on purpose.
        _crud = new CrudHandler<ActivityEntity>(store, logger, "activities", ValidateAsync,
            beforeReplace: conversion.EnsureConvertedAsync);
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
        Guid id, CancellationToken ct) => RouteFailures.RunAsync(_logger, "DeleteActivity", ct, async () =>
    {
        // No conversion first: the row goes, and its unconverted old text going with it is the
        // user's intent.
        var result = await _crud.DeleteAsync(req, id, "DeleteActivity", ct);
        // Deleting an Activity deletes its Comments (ADR 0001), only once the Activity delete
        // answered 204: a stale If-Match (409) deletes nothing. A 204 for a row that was already
        // gone still cleans up, so retrying a delete whose cleanup failed removes what was left.
        if (result is NoContentResult)
            await CommentFunctions.DeleteAllForActivityAsync(_comments, id, ct);
        return result;
    });

    [Function("DuplicateActivity")]
    public Task<IActionResult> Duplicate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "activities/{id:guid}/duplicate")] HttpRequest req,
        Guid id, CancellationToken ct) => RouteFailures.RunAsync(_logger, "DuplicateActivity", ct, async () =>
    {
        var source = await _store.GetAsync(id, ct);
        if (source is null) return new NotFoundResult();

        // A duplicate starts with no Comments: Comments are rows of their own and belong to the original.
        var duplicate = new ActivityEntity
        {
            Id = Guid.NewGuid(),
            GameId = source.GameId,
            Name = $"{source.Name} (Copy)",
            Description = source.Description,
            Rules = source.Rules,
            ThemeIds = [.. source.ThemeIds],
            HolidayIds = [.. source.HolidayIds],
            SetupRequirements = source.SetupRequirements
        };

        // A new id, so this never replaces a row that still holds an old text: no conversion first.
        await _store.UpsertAsync(duplicate, ct);
        return EntityResults.Created(req, $"/api/activities/{duplicate.Id}", duplicate);
    });
}
