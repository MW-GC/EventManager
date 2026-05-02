using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.API.Functions;

internal sealed class ThemeFunctions
{
    private readonly TableStore<ThemeEntity> _store;

    public ThemeFunctions(TableStore<ThemeEntity> store) => _store = store;

    [Function("GetThemes")]
    public async Task<IActionResult> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "themes")] HttpRequest req,
        CancellationToken ct)
    {
        var themes = await _store.GetAllAsync(ct);
        return new OkObjectResult(themes);
    }

    [Function("CreateTheme")]
    public async Task<IActionResult> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "themes")] HttpRequest req,
        CancellationToken ct)
    {
        var entity = await req.ReadFromJsonAsync<ThemeEntity>(ct);
        if (entity is null) return new BadRequestResult();

        entity.Id = Guid.NewGuid();
        await _store.UpsertAsync(entity, ct);
        return new CreatedResult($"/api/themes/{entity.Id}", entity);
    }

    [Function("UpdateTheme")]
    public async Task<IActionResult> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "themes/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct)
    {
        var existing = await _store.GetAsync(id, ct);
        if (existing is null) return new NotFoundResult();

        var entity = await req.ReadFromJsonAsync<ThemeEntity>(ct);
        if (entity is null) return new BadRequestResult();

        entity.Id = id;
        await _store.UpsertAsync(entity, ct);
        return new OkObjectResult(entity);
    }

    [Function("DeleteTheme")]
    public async Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "themes/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct)
    {
        await _store.DeleteAsync(id, ct);
        return new NoContentResult();
    }
}
