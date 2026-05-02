namespace MW_GC.EventManager.Shared.Models;

public record Event
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public DateTimeOffset Date { get; init; } = DateTimeOffset.UtcNow;
    public List<Selection> Selections { get; init; } = [];
    public Game? Winner { get; init; }
    public bool UniqueGamesOnly { get; init; } = true;
}
