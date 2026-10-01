namespace MW_GC.EventManager.Shared.Requests;

public record GenerateEventRequest
{
    /// <summary>Largest accepted <see cref="UtcOffsetMinutes"/> magnitude: UTC-14:00 to UTC+14:00.</summary>
    public const int MaximumUtcOffsetMinutes = 840;

    public int Count { get; init; } = 3;
    public bool ThemedOnly { get; init; }
    public List<Guid> SelectedGameIds { get; init; } = [];
    public List<Guid> SelectedThemeIds { get; init; } = [];
    public List<Guid> SelectedHolidayIds { get; init; } = [];
    public bool UniqueGamesOnly { get; init; } = true;

    /// <summary>
    /// The caller's local offset from UTC in minutes (UTC-05:00 is -300), used only to name the
    /// generated Event. Null names it in UTC. The stored Date is always the UTC instant.
    /// </summary>
    public int? UtcOffsetMinutes { get; init; }
}
