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
    private readonly CrudHandler<ThemeEntity> _crud;

    public ThemeFunctions(TableStore<ThemeEntity> store, TableStore<ActivityEntity> activities, ILogger<ThemeFunctions> logger)
    {
        // A missing Theme stays an idempotent 204. A Theme that Activities still list is refused
        // (409) and nothing is deleted, so no Activity is left with an id that names nothing.
        _crud = new CrudHandler<ThemeEntity>(store, logger, "themes", CrudHandler<ThemeEntity>.Sync(ThemeValidator.Validate),
            async (id, ct) => (await activities.GetAllAsync(ct)).Count(a => a.ThemeIds.Contains(id)) is var inUse and > 0
                ? InUseMessage(inUse)
                : null);
    }

    [Function("GetThemes")]
    public Task<IActionResult> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "themes")] HttpRequest req,
        CancellationToken ct) => _crud.GetAllAsync("GetThemes", ct);

    [Function("CreateTheme")]
    public Task<IActionResult> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "themes")] HttpRequest req,
        CancellationToken ct) => _crud.CreateAsync(req, "CreateTheme", ct);

    [Function("UpdateTheme")]
    public Task<IActionResult> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "themes/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct) => _crud.UpdateAsync(req, id, "UpdateTheme", ct);

    [Function("DeleteTheme")]
    public Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "themes/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct) => _crud.DeleteAsync(req, id, "DeleteTheme", ct);

    internal static string InUseMessage(int activities) => activities == 1
        ? "This theme is used by 1 activity. Remove it from that activity first."
        : $"This theme is used by {activities} activities. Remove it from them first.";
}
