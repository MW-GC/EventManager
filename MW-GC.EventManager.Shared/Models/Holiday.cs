namespace MW_GC.EventManager.Shared.Models;

public record Holiday
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
}
