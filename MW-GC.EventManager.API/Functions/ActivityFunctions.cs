using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.API.Functions;

internal sealed class ActivityFunctions
{
    private readonly TableStore<ActivityEntity> _store;

    public ActivityFunctions(TableStore<ActivityEntity> store) => _store = store;

    [Function("GetActivities")]
    public async Task<IActionResult> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "activities")] HttpRequest req,
        CancellationToken ct)
    {
        var activities = await _store.GetAllAsync(ct);
        return new OkObjectResult(activities);
    }

    [Function("GetActivity")]
    public async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "activities/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct)
    {
        var activity = await _store.GetAsync(id, ct);
        return activity is null ? new NotFoundResult() : new OkObjectResult(activity);
    }

    [Function("CreateActivity")]
    public async Task<IActionResult> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "activities")] HttpRequest req,
        CancellationToken ct)
    {
        var entity = await req.ReadFromJsonAsync<ActivityEntity>(ct);
        if (entity is null) return new BadRequestResult();

        entity.Id = Guid.NewGuid();
        await _store.UpsertAsync(entity, ct);
        return new CreatedResult($"/api/activities/{entity.Id}", entity);
    }

    [Function("UpdateActivity")]
    public async Task<IActionResult> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "activities/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct)
    {
        var existing = await _store.GetAsync(id, ct);
        if (existing is null) return new NotFoundResult();

        var entity = await req.ReadFromJsonAsync<ActivityEntity>(ct);
        if (entity is null) return new BadRequestResult();

        entity.Id = id;
        await _store.UpsertAsync(entity, ct);
        return new OkObjectResult(entity);
    }

    [Function("DeleteActivity")]
    public async Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "activities/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct)
    {
        await _store.DeleteAsync(id, ct);
        return new NoContentResult();
    }

    [Function("DuplicateActivity")]
    public async Task<IActionResult> Duplicate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "activities/{id:guid}/duplicate")] HttpRequest req,
        Guid id, CancellationToken ct)
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
    }

    [Function("UpdateActivityComments")]
    public async Task<IActionResult> UpdateComments(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "activities/{id:guid}/comments")] HttpRequest req,
        Guid id, CancellationToken ct)
    {
        var existing = await _store.GetAsync(id, ct);
        if (existing is null) return new NotFoundResult();

        var body = await req.ReadFromJsonAsync<CommentsPayload>(ct);
        if (body is null) return new BadRequestResult();

        existing.Comments = body.Comments;
        await _store.UpsertAsync(existing, ct);
        return new OkObjectResult(existing);
    }

    private record CommentsPayload(string? Comments);
}
