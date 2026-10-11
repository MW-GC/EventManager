using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.API.Functions;

internal sealed class HolidayFunctions
{
    private readonly CrudHandler<HolidayEntity> _crud;

    public HolidayFunctions(TableStore<HolidayEntity> store, TableStore<ActivityEntity> activities, ILogger<HolidayFunctions> logger)
    {
        // A missing Holiday stays an idempotent 204. A Holiday that Activities still list is
        // refused (409) and nothing is deleted, so no Activity is left with an id that names nothing.
        _crud = new CrudHandler<HolidayEntity>(store, logger, "holidays", CrudHandler<HolidayEntity>.Sync(HolidayValidator.Validate),
            async (id, ct) => (await activities.GetAllAsync(ct)).Count(a => a.HolidayIds.Contains(id)) is var inUse and > 0
                ? InUseMessage(inUse)
                : null);
    }

    [Function("GetHolidays")]
    public Task<IActionResult> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "holidays")] HttpRequest req,
        CancellationToken ct) => _crud.GetAllAsync("GetHolidays", ct);

    [Function("CreateHoliday")]
    public Task<IActionResult> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "holidays")] HttpRequest req,
        CancellationToken ct) => _crud.CreateAsync(req, "CreateHoliday", ct);

    [Function("UpdateHoliday")]
    public Task<IActionResult> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "holidays/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct) => _crud.UpdateAsync(req, id, "UpdateHoliday", ct);

    [Function("DeleteHoliday")]
    public Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "holidays/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct) => _crud.DeleteAsync(req, id, "DeleteHoliday", ct);

    internal static string InUseMessage(int activities) => activities == 1
        ? "This holiday is used by 1 activity. Remove it from that activity first."
        : $"This holiday is used by {activities} activities. Remove it from them first.";
}
