using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.API.Functions;

internal sealed class ThemeFunctions
{
    private readonly TableStore<ThemeEntity> _store;
    private readonly TableStore<ActivityEntity> _activities;
    private readonly ILogger<ThemeFunctions> _logger;

    public ThemeFunctions(TableStore<ThemeEntity> store, TableStore<ActivityEntity> activities, ILogger<ThemeFunctions> logger)
    {
        _store = store;
        _activities = activities;
        _logger = logger;
    }

    [Function("GetThemes")]
    public Task<IActionResult> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "themes")] HttpRequest req,
        CancellationToken ct) => RouteFailures.RunAsync(_logger, "GetThemes", ct, async () =>
    {
        var themes = await _store.GetAllAsync(ct);
        return new OkObjectResult(themes);
    });

    [Function("CreateTheme")]
    public Task<IActionResult> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "themes")] HttpRequest req,
        CancellationToken ct) => RouteFailures.RunAsync(_logger, "CreateTheme", ct, async () =>
    {
        var body = await RequestBody.ReadAsync<ThemeEntity>(req, ct);
        if (!body.Ok) return body.Error;
        var entity = body.Value;
        if (ThemeValidator.Validate(entity) is { } error) return new BadRequestObjectResult(error);

        entity.Id = Guid.NewGuid();
        await _store.UpsertAsync(entity, ct);
        return EntityResults.Created(req, $"/api/themes/{entity.Id}", entity);
    });

    [Function("UpdateTheme")]
    public Task<IActionResult> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "themes/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct) => RouteFailures.RunAsync(_logger, "UpdateTheme", ct, async () =>
    {
        var existing = await _store.GetAsync(id, ct);
        if (existing is null) return new NotFoundResult();

        var body = await RequestBody.ReadAsync<ThemeEntity>(req, ct);
        if (!body.Ok) return body.Error;
        var entity = body.Value;
        if (ThemeValidator.Validate(entity) is { } error) return new BadRequestObjectResult(error);

        entity.Id = id;
        return await EntityResults.UpdateAsync(_store, req, entity, ct);
    });

    [Function("DeleteTheme")]
    public Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "themes/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct) => RouteFailures.RunAsync(_logger, "DeleteTheme", ct, async () =>
    {
        // A missing Theme stays an idempotent 204. A Theme that Activities still list is refused
        // (409) and nothing is deleted, so no Activity is left with an id that names nothing.
        if (await _store.GetAsync(id, ct) is null) return new NoContentResult();

        var inUse = (await _activities.GetAllAsync(ct)).Count(a => a.ThemeIds.Contains(id));
        if (inUse > 0) return new ConflictObjectResult(InUseMessage(inUse));

        return await EntityResults.DeleteAsync(_store, req, id, ct);
    });

    internal static string InUseMessage(int activities) => activities == 1
        ? "This theme is used by 1 activity. Remove it from that activity first."
        : $"This theme is used by {activities} activities. Remove it from them first.";
}
