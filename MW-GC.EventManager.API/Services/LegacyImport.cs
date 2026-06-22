using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.API.Services;

/// <summary>
/// Shape of the legacy app's JSON export. Field names and string ids differ from the
/// current entity model (e.g. <c>themes</c>/<c>holidays</c> vs. <c>ThemeIds</c>/<c>HolidayIds</c>,
/// and the legacy-only <c>themed</c> flag), so these DTOs deserialize the old format
/// before <see cref="LegacyImportMapper"/> translates it into entities.
/// </summary>
internal sealed record LegacyImport
{
    public List<LegacyGame> Games { get; init; } = [];
    public List<LegacyActivity> Activities { get; init; } = [];
    public List<LegacyEvent> Events { get; init; } = [];
    public List<LegacyTheme> Themes { get; init; } = [];
    public List<LegacyHoliday> Holidays { get; init; } = [];
}

internal sealed record LegacyGame
{
    public string? Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? ImageUrl { get; init; }
    public string? Website { get; init; }
    public string? IconUrl { get; init; }
}

internal sealed record LegacyActivity
{
    public string? Id { get; init; }
    public string? GameId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Rules { get; init; } = string.Empty;

    /// <summary>Legacy theme string ids (mapped to <c>ThemeIds</c>).</summary>
    public List<string> Themes { get; init; } = [];

    /// <summary>Legacy holiday string ids (mapped to <c>HolidayIds</c>).</summary>
    public List<string> Holidays { get; init; } = [];

    public string SetupRequirements { get; init; } = string.Empty;
    public string? Comments { get; init; }
}

internal sealed record LegacyEvent
{
    public string? Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public DateTimeOffset Date { get; init; }
    public List<LegacySelection> Selections { get; init; } = [];

    /// <summary>Legacy root-level winner reference (a game id). Resolved to the winning
    /// selection's activity and stored as <c>WinnerActivityId</c>.</summary>
    public string? WinnerId { get; init; }

    public bool UniqueGamesOnly { get; init; }
}

internal sealed record LegacySelection
{
    public LegacyGame? Game { get; init; }
    public LegacyActivity? Activity { get; init; }
}

internal sealed record LegacyTheme
{
    public string? Id { get; init; }
    public string Name { get; init; } = string.Empty;
}

internal sealed record LegacyHoliday
{
    public string? Id { get; init; }
    public string Name { get; init; } = string.Empty;
}

/// <summary>Entities produced from a legacy import, ready to persist.</summary>
internal sealed record ImportResult(
    List<GameEntity> Games,
    List<ActivityEntity> Activities,
    List<EventEntity> Events,
    List<ThemeEntity> Themes,
    List<HolidayEntity> Holidays);

/// <summary>Per-collection counts returned to the caller after a successful import.</summary>
internal sealed record ImportSummary(int Games, int Activities, int Events, int Themes, int Holidays);
