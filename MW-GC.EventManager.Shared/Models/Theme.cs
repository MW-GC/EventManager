namespace MW_GC.EventManager.Shared.Models;

public record Theme
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
}
