namespace MW_GC.EventManager.Shared.Requests;

public record GenerateEventRequest
{
    public int Count { get; init; } = 3;
    public bool ThemedOnly { get; init; }
    public List<Guid> SelectedThemeIds { get; init; } = [];
    public List<Guid> SelectedHolidayIds { get; init; } = [];
    public bool UniqueGamesOnly { get; init; } = true;
}
