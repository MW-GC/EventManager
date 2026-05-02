namespace MW_GC.EventManager.Shared.Models;

public record Selection
{
    public required Game Game { get; init; }
    public required Activity Activity { get; init; }
}
