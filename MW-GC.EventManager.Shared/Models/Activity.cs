namespace MW_GC.EventManager.Shared.Models;

public record Activity
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>FK to <see cref="Game.Id"/>. Required.</summary>
    public required Guid GameId { get; init; }

    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Rules { get; init; } = string.Empty;

    /// <summary>FK list to <see cref="Theme.Id"/>.</summary>
    public List<Guid> ThemeIds { get; init; } = [];

    /// <summary>FK list to <see cref="Holiday.Id"/>.</summary>
    public List<Guid> HolidayIds { get; init; } = [];

    public string SetupRequirements { get; init; } = string.Empty;

    /// <summary>
    /// Players and duration as the Activity had them when the Selection was chosen (#98). A snapshot stored
    /// before they existed has no such values and reads back with all three null.
    /// </summary>
    public int? MinPlayers { get; init; }
    public int? MaxPlayers { get; init; }
    public int? DurationMinutes { get; init; }

    // Also the shape of the Activity snapshot in an Event's Selection. It carries no Comments
    // (#133): those are read live from the Activity. A snapshot stored earlier with a "comments"
    // value still loads, because the serializer skips a property this record does not have, and
    // the next save of that Event writes the snapshot without it.
}
