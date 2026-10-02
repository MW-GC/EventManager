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
    private readonly ILogger<ThemeFunctions> _logger;

    public ThemeFunctions(TableStore<ThemeEntity> store, ILogger<ThemeFunctions> logger)
    {
        _store = store;
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
        return new CreatedResult($"/api/themes/{entity.Id}", entity);
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
        await _store.UpsertAsync(entity, ct);
        return new OkObjectResult(entity);
    });

    [Function("DeleteTheme")]
    public Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "themes/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct) => RouteFailures.RunAsync(_logger, "DeleteTheme", ct, async () =>
    {
        await _store.DeleteAsync(id, ct);
        return new NoContentResult();
    });
}
