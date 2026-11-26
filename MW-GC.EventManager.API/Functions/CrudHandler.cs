using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.API.Functions;

/// <summary>
/// The get-all, get-one, create, update and delete routes shared by the Game, Activity, Theme and
/// Holiday Functions classes. Each class keeps its own trigger shims and passes its operation name,
/// so routes, function names and log entries stay exactly as they were; this class only removes
/// the copies. Every route runs through <see cref="RouteFailures.RunAsync"/> with the owner's logger.
/// </summary>
/// <param name="store">The entity's table.</param>
/// <param name="logger">The owning Functions class's logger.</param>
/// <param name="resource">The route segment, used for the <c>Location</c> of a create (<c>/api/{resource}/{id}</c>).</param>
/// <param name="validate">Runs on create and update after the body is read; returns the 400 message or null.</param>
/// <param name="deleteRefusal">
/// Optional. When given, deleting a missing row is an idempotent 204 without touching storage, and a
/// non-null message refuses the delete with a 409 carrying that message; nothing is deleted.
/// </param>
internal sealed class CrudHandler<TEntity>(
    TableStore<TEntity> store,
    ILogger logger,
    string resource,
    Func<TEntity, CancellationToken, Task<string?>> validate,
    Func<Guid, CancellationToken, Task<string?>>? deleteRefusal = null)
    where TEntity : EntityBase, new()
{
    /// <summary>Wraps a synchronous validator for the <c>validate</c> step.</summary>
    public static Func<TEntity, CancellationToken, Task<string?>> Sync(Func<TEntity, string?> validate) =>
        (entity, _) => Task.FromResult(validate(entity));

    public Task<IActionResult> GetAllAsync(string operation, CancellationToken ct) =>
        RouteFailures.RunAsync(logger, operation, ct, async () =>
        {
            var all = await store.GetAllAsync(ct);
            return new OkObjectResult(all);
        });

    public Task<IActionResult> GetAsync(HttpRequest req, Guid id, string operation, CancellationToken ct) =>
        RouteFailures.RunAsync(logger, operation, ct, async () =>
        {
            var entity = await store.GetAsync(id, ct);
            return entity is null ? new NotFoundResult() : EntityResults.Ok(req, entity);
        });

    public Task<IActionResult> CreateAsync(HttpRequest req, string operation, CancellationToken ct) =>
        RouteFailures.RunAsync(logger, operation, ct, async () =>
        {
            var body = await RequestBody.ReadAsync<TEntity>(req, ct);
            if (!body.Ok) return body.Error;
            var entity = body.Value;
            if (await validate(entity, ct) is { } error) return new BadRequestObjectResult(error);

            entity.Id = Guid.NewGuid();
            await store.UpsertAsync(entity, ct);
            return EntityResults.Created(req, $"/api/{resource}/{entity.Id}", entity);
        });

    public Task<IActionResult> UpdateAsync(HttpRequest req, Guid id, string operation, CancellationToken ct) =>
        RouteFailures.RunAsync(logger, operation, ct, async () =>
        {
            var existing = await store.GetAsync(id, ct);
            if (existing is null) return new NotFoundResult();

            var body = await RequestBody.ReadAsync<TEntity>(req, ct);
            if (!body.Ok) return body.Error;
            var entity = body.Value;
            if (await validate(entity, ct) is { } error) return new BadRequestObjectResult(error);

            entity.Id = id;
            return await EntityResults.UpdateAsync(store, req, entity, ct);
        });

    public Task<IActionResult> DeleteAsync(HttpRequest req, Guid id, string operation, CancellationToken ct) =>
        RouteFailures.RunAsync(logger, operation, ct, async () =>
        {
            if (deleteRefusal is not null)
            {
                if (await store.GetAsync(id, ct) is null) return new NoContentResult();
                if (await deleteRefusal(id, ct) is { } refusal) return new ConflictObjectResult(refusal);
            }

            return await EntityResults.DeleteAsync(store, req, id, ct);
        });
}
