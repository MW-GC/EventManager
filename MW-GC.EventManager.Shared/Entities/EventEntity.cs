using MW_GC.EventManager.Shared.Models;

namespace MW_GC.EventManager.Shared.Entities;

public class EventEntity : EntityBase
{
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset Date { get; set; }
    public List<Selection> Selections { get; set; } = [];
    public Guid? WinnerGameId { get; set; }
    public bool UniqueGamesOnly { get; set; } = true;
}
