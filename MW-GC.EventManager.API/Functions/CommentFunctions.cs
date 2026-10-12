using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.API.Functions;

/// <summary>
/// An Activity's Comments (ADR 0001): each Comment is its own row in the <c>ActivityComments</c>
/// table with its own eTag, read and written under its Activity. The API sets the added date
/// (<see cref="CommentEntity.CreatedUtc"/>) and an edit changes only the text. Deleting the
/// Activity deletes its Comments (see <see cref="ActivityFunctions"/>); deleting an Event does not.
/// </summary>
internal sealed class CommentFunctions
{
    private readonly TableStore<CommentEntity> _store;
    private readonly TableStore<ActivityEntity> _activities;
    private readonly TableStore<EventEntity> _events;
    private readonly ILogger<CommentFunctions> _logger;

    public CommentFunctions(
        TableStore<CommentEntity> store,
        TableStore<ActivityEntity> activities,
        TableStore<EventEntity> events,
        ILogger<CommentFunctions> logger)
    {
        _store = store;
        _activities = activities;
        _events = events;
        _logger = logger;
    }

    [Function("ListComments")]
    public Task<IActionResult> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "activities/{id:guid}/comments")] HttpRequest req,
        Guid id, CancellationToken ct) => RouteFailures.RunAsync(_logger, "ListComments", ct, async () =>
    {
        if (await _activities.GetAsync(id, ct) is null) return new NotFoundResult();
        return new OkObjectResult(await ForActivityAsync(_store, id, ct));
    });

    [Function("AddComment")]
    public Task<IActionResult> Add(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "activities/{id:guid}/comments")] HttpRequest req,
        Guid id, CancellationToken ct) => RouteFailures.RunAsync(_logger, "AddComment", ct, async () =>
    {
        if (await _activities.GetAsync(id, ct) is null) return new NotFoundResult();

        var body = await RequestBody.ReadAsync<AddPayload>(req, ct);
        if (!body.Ok) return body.Error;
        if (CommentValidator.ValidateText(body.Value.Text, out var text) is { } error) return new BadRequestObjectResult(error);
        if (body.Value.EventId is { } eventId && await _events.GetAsync(eventId, ct) is null)
            return new BadRequestObjectResult(CommentValidator.EventNotFoundMessage);

        // The id, the Activity and the added date come from the API, never from the body.
        var comment = new CommentEntity
        {
            Id = Guid.NewGuid(),
            ActivityId = id,
            Text = text,
            CreatedUtc = DateTimeOffset.UtcNow,
            EventId = body.Value.EventId,
        };
        await _store.UpsertAsync(comment, ct);
        return EntityResults.Created(req, $"/api/activities/{id}/comments/{comment.Id}", comment);
    });

    [Function("UpdateComment")]
    public Task<IActionResult> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "activities/{id:guid}/comments/{commentId:guid}")] HttpRequest req,
        Guid id, Guid commentId, CancellationToken ct) => RouteFailures.RunAsync(_logger, "UpdateComment", ct, async () =>
    {
        var existing = await _store.GetAsync(commentId, ct);
        if (existing is null || existing.ActivityId != id) return new NotFoundResult();

        var body = await RequestBody.ReadAsync<UpdatePayload>(req, ct);
        if (!body.Ok) return body.Error;
        if (CommentValidator.ValidateText(body.Value.Text, out var text) is { } error) return new BadRequestObjectResult(error);

        // Only the text changes: the added date, the Activity and the Event are kept from the row.
        var edited = new CommentEntity
        {
            Id = commentId,
            ActivityId = existing.ActivityId,
            Text = text,
            CreatedUtc = existing.CreatedUtc,
            EventId = existing.EventId,
        };
        return await EntityResults.UpdateAsync(_store, req, edited, ct);
    });

    [Function("DeleteComment")]
    public Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "activities/{id:guid}/comments/{commentId:guid}")] HttpRequest req,
        Guid id, Guid commentId, CancellationToken ct) => RouteFailures.RunAsync(_logger, "DeleteComment", ct, async () =>
    {
        // Idempotent like every other delete: a missing Comment is a 204, and so is a Comment of
        // another Activity, which this route never deletes.
        var existing = await _store.GetAsync(commentId, ct);
        if (existing is null || existing.ActivityId != id) return new NoContentResult();
        return await EntityResults.DeleteAsync(_store, req, commentId, ct);
    });

    /// <summary>Deletes every Comment of the Activity. Used by the Activity delete.</summary>
    internal static async Task DeleteAllForActivityAsync(TableStore<CommentEntity> store, Guid activityId, CancellationToken ct)
    {
        foreach (var comment in await ForActivityAsync(store, activityId, ct))
            await store.DeleteAsync(comment.Id, ct: ct);
    }

    // The ActivityId filter is part of the table query, so a list or an Activity delete reads only
    // that Activity's Comments, not the whole Comment partition.
    private static async Task<List<CommentEntity>> ForActivityAsync(
        TableStore<CommentEntity> store, Guid activityId, CancellationToken ct) =>
        (await store.GetWhereAsync(c => c.ActivityId == activityId, ct))
            .OrderBy(c => c.CreatedUtc)
            .ThenBy(c => c.Id)
            .ToList();

    private record AddPayload(string? Text, Guid? EventId);

    private record UpdatePayload(string? Text);
}
