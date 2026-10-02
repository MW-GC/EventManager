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
    private readonly TableStore<HolidayEntity> _store;
    private readonly ILogger<HolidayFunctions> _logger;

    public HolidayFunctions(TableStore<HolidayEntity> store, ILogger<HolidayFunctions> logger)
    {
        _store = store;
        _logger = logger;
    }

    [Function("GetHolidays")]
    public Task<IActionResult> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "holidays")] HttpRequest req,
        CancellationToken ct) => RouteFailures.RunAsync(_logger, "GetHolidays", ct, async () =>
    {
        var holidays = await _store.GetAllAsync(ct);
        return new OkObjectResult(holidays);
    });

    [Function("CreateHoliday")]
    public Task<IActionResult> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "holidays")] HttpRequest req,
        CancellationToken ct) => RouteFailures.RunAsync(_logger, "CreateHoliday", ct, async () =>
    {
        var body = await RequestBody.ReadAsync<HolidayEntity>(req, ct);
        if (!body.Ok) return body.Error;
        var entity = body.Value;
        if (HolidayValidator.Validate(entity) is { } error) return new BadRequestObjectResult(error);

        entity.Id = Guid.NewGuid();
        await _store.UpsertAsync(entity, ct);
        return new CreatedResult($"/api/holidays/{entity.Id}", entity);
    });

    [Function("UpdateHoliday")]
    public Task<IActionResult> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "holidays/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct) => RouteFailures.RunAsync(_logger, "UpdateHoliday", ct, async () =>
    {
        var existing = await _store.GetAsync(id, ct);
        if (existing is null) return new NotFoundResult();

        var body = await RequestBody.ReadAsync<HolidayEntity>(req, ct);
        if (!body.Ok) return body.Error;
        var entity = body.Value;
        if (HolidayValidator.Validate(entity) is { } error) return new BadRequestObjectResult(error);

        entity.Id = id;
        await _store.UpsertAsync(entity, ct);
        return new OkObjectResult(entity);
    });

    [Function("DeleteHoliday")]
    public Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "holidays/{id:guid}")] HttpRequest req,
        Guid id, CancellationToken ct) => RouteFailures.RunAsync(_logger, "DeleteHoliday", ct, async () =>
    {
        await _store.DeleteAsync(id, ct);
        return new NoContentResult();
    });
}
