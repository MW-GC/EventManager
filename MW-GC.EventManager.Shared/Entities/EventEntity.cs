using MW_GC.EventManager.Shared.Models;

namespace MW_GC.EventManager.Shared.Entities;

public class EventEntity : EntityBase
{
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset Date { get; set; }
    public List<Selection> Selections { get; set; } = [];

    /// <summary>
    /// FK to the winning <see cref="Selection"/> via its <see cref="Activity.Id"/>. An activity
    /// id is unique within an event's selections, so it identifies the full game+activity context
    /// of the winner (a game id cannot, since a game may appear in more than one selection).
    /// </summary>
    public Guid? WinnerActivityId { get; set; }
    public bool UniqueGamesOnly { get; set; } = true;
}
