namespace MW_GC.EventManager.Shared.Models;

public record Game
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public string? ImageUrl { get; init; }
    public string? Website { get; init; }
    public string? IconUrl { get; init; }
}
