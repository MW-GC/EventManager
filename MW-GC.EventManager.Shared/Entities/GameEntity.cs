namespace MW_GC.EventManager.Shared.Entities;

public class GameEntity : EntityBase
{
    public string Name { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public string? Website { get; set; }
    public string? IconUrl { get; set; }
}
