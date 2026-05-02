using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.API.Functions;

internal sealed class HolidayFunctions
{
    private readonly TableStore<HolidayEntity> _store;

    public HolidayFunctions(TableStore<HolidayEntity> store) => _store = store;

    [Function("GetHolidays")]
    public async Task<IActionResult> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "holidays")] HttpRequest req,
        CancellationToken ct)
    {
        var holidays = await _store.GetAllAsync(ct);
        return new OkObjectResult(holidays);
    }

    [Function("CreateHoliday")]
    public async Task<IActionResult> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "holidays")] HttpRequest req,
        CancellationToken ct)
    {
        var entity = await req.ReadFromJsonAsync<HolidayEntity>(ct);
        if (entity is null) return new BadRequestResult();

        entity.Id = Guid.NewGuid();
        await _store.UpsertAsync(entity, ct);
        return new CreatedResult($"/api/holidays/{entity.Id}", entity);
    }

    [Function("UpdateHoliday")]
    public async Task<IActionResult> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "holidays/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct)
    {
        var existing = await _store.GetAsync(id, ct);
        if (existing is null) return new NotFoundResult();

        var entity = await req.ReadFromJsonAsync<HolidayEntity>(ct);
        if (entity is null) return new BadRequestResult();

        entity.Id = id;
        await _store.UpsertAsync(entity, ct);
        return new OkObjectResult(entity);
    }

    [Function("DeleteHoliday")]
    public async Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "holidays/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct)
    {
        await _store.DeleteAsync(id, ct);
        return new NoContentResult();
    }
}
