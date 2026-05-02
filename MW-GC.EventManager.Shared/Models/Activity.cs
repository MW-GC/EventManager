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
    public string? Comments { get; init; }
}
