using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.API.Functions;

internal sealed class ImportFunctions
{
    private readonly TableStore<GameEntity> _games;
    private readonly TableStore<ActivityEntity> _activities;
    private readonly TableStore<EventEntity> _events;
    private readonly TableStore<ThemeEntity> _themes;
    private readonly TableStore<HolidayEntity> _holidays;

    public ImportFunctions(
        TableStore<GameEntity> games,
        TableStore<ActivityEntity> activities,
        TableStore<EventEntity> events,
        TableStore<ThemeEntity> themes,
        TableStore<HolidayEntity> holidays)
    {
        _games = games;
        _activities = activities;
        _events = events;
        _themes = themes;
        _holidays = holidays;
    }

    /// <summary>
    /// Imports a legacy JSON export. Records are translated to the current model with
    /// deterministic ids and upserted, so the operation is idempotent — re-importing the
    /// same file updates existing rows rather than creating duplicates. Existing data that
    /// isn't in the payload is left untouched.
    /// </summary>
    [Function("ImportData")]
    public async Task<IActionResult> Import(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "import")] HttpRequest req,
        CancellationToken ct)
    {
        LegacyImport? payload;
        try
        {
            payload = await req.ReadFromJsonAsync<LegacyImport>(ct);
        }
        catch (JsonException)
        {
            return new BadRequestObjectResult("Request body is not valid JSON.");
        }

        if (payload is null) return new BadRequestResult();

        var mapped = LegacyImportMapper.Map(payload);

        // Persist lookups first, then games, activities, and finally events.
        foreach (var theme in mapped.Themes) await _themes.UpsertAsync(theme, ct);
        foreach (var holiday in mapped.Holidays) await _holidays.UpsertAsync(holiday, ct);
        foreach (var game in mapped.Games) await _games.UpsertAsync(game, ct);
        foreach (var activity in mapped.Activities) await _activities.UpsertAsync(activity, ct);
        foreach (var evt in mapped.Events) await _events.UpsertAsync(evt, ct);

        var summary = new ImportSummary(
            mapped.Games.Count,
            mapped.Activities.Count,
            mapped.Events.Count,
            mapped.Themes.Count,
            mapped.Holidays.Count);

        return new OkObjectResult(summary);
    }
}
