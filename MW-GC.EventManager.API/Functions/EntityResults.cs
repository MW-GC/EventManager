using Azure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.API.Functions;

/// <summary>
/// Optimistic concurrency on the wire. Every single-entity response carries the row's ETag in the
/// ETag header (the JSON body carries it too, as <c>eTag</c>). A client may send it back as
/// If-Match on PUT, DELETE and the comments PATCH; a stale tag is a 409 and nothing is written.
/// If-Match is optional: without it an update still never recreates a row that is gone.
/// Only the header counts; an <c>eTag</c> in a request body is ignored.
/// </summary>
internal static class EntityResults
{
    /// <summary>The If-Match header as given (not parsed), or a default ETag when there is none.</summary>
    public static ETag IfMatch(HttpRequest req)
    {
        var value = req.Headers[HeaderNames.IfMatch].ToString();
        return string.IsNullOrWhiteSpace(value) ? default : new ETag(value.Trim());
    }

    public static IActionResult Ok(HttpRequest req, EntityBase entity)
    {
        SetETag(req, entity);
        return new OkObjectResult(entity);
    }

    public static IActionResult Created(HttpRequest req, string location, EntityBase entity)
    {
        SetETag(req, entity);
        return new CreatedResult(location, entity);
    }

    /// <summary>The same 409 body a storage conflict gets: the caller reloads and tries again.</summary>
    public static IActionResult Conflict() =>
        new ObjectResult(RouteFailures.ConflictMessage) { StatusCode = StatusCodes.Status409Conflict };

    /// <summary>
    /// Replaces an existing row (conditionally when the request has If-Match) and answers 200, 404 or 409.
    /// An <c>eTag</c> that arrived in the request body is dropped: only the If-Match header counts.
    /// </summary>
    public static async Task<IActionResult> UpdateAsync<TEntity>(
        TableStore<TEntity> store, HttpRequest req, TEntity entity, CancellationToken ct, ETag? ifMatch = null)
        where TEntity : EntityBase, new()
    {
        var condition = ifMatch ?? IfMatch(req);
        entity.ETag = default;
        return await store.UpdateAsync(entity, condition, ct) switch
        {
            UpdateOutcome.Updated => Ok(req, entity),
            UpdateOutcome.NotFound => new NotFoundResult(),
            _ => Conflict(),
        };
    }

    /// <summary>Deletes a row (conditionally when the request has If-Match): 204, or 409 for a stale tag.</summary>
    public static async Task<IActionResult> DeleteAsync<TEntity>(
        TableStore<TEntity> store, HttpRequest req, Guid id, CancellationToken ct)
        where TEntity : EntityBase, new() =>
        await store.DeleteAsync(id, IfMatch(req), ct) == DeleteOutcome.Conflict
            ? Conflict()
            : new NoContentResult();

    private static void SetETag(HttpRequest req, EntityBase entity)
    {
        if (entity.ETag != default)
            req.HttpContext.Response.Headers[HeaderNames.ETag] = entity.ETag.ToString();
    }
}
