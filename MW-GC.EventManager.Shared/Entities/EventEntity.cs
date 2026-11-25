using System.Text.Json.Serialization;
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

    // The Occurrence fields (#204, ADR 0002). A standalone Event has none of them, and they are left out
    // of its JSON, so a standalone Event reads and writes exactly as before.

    /// <summary>The Series this Event is an Occurrence of; null for a standalone Event. Set by the API only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? SeriesId { get; set; }

    /// <summary>The instant the Occurrence was generated for (its occurrence key). Never changes, even when its date is edited.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? OccurrenceKey { get; set; }

    /// <summary>Set when an admin edits the Occurrence by hand; a Series edit never rewrites it after that.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Modified { get; set; }

    public const int MaximumSelections = 5;

    /// <summary>
    /// Validate selection snapshots before normalizing or persisting an event. A standalone Event has 1 to
    /// <see cref="MaximumSelections"/> Selections; an Occurrence (<see cref="SeriesId"/> set) may have none
    /// until it is rolled.
    /// </summary>
    public string? ValidateSelections()
    {
        if (SeriesId is not null && Selections is { Count: 0 })
            return null;
        if (Selections is null || Selections.Count is < 1 or > MaximumSelections)
            return $"Select between 1 and {MaximumSelections} activities.";
        if (Selections.Any(s => s is null || s.Game is null || s.Activity is null
            || s.Game.Id == Guid.Empty || s.Activity.Id == Guid.Empty
            || s.Activity.GameId != s.Game.Id))
            return "Each selection must contain an activity and its matching game.";
        if (Selections.Select(s => s.Activity.Id).Distinct().Count() != Selections.Count)
            return "An activity may only be selected once.";
        if (UniqueGamesOnly && Selections.Select(s => s.Game.Id).Distinct().Count() != Selections.Count)
            return "Each selection must use a different game when unique games are required.";
        return null;
    }

    /// <summary>
    /// Apply winner rules to the final selections before saving. With no Selections (an Occurrence not
    /// rolled yet) there is no Winner.
    /// </summary>
    public void NormalizeWinner()
    {
        if (Selections.Count == 1)
            WinnerActivityId = Selections[0].Activity.Id;
        else if (WinnerActivityId.HasValue && !Selections.Any(s => s.Activity.Id == WinnerActivityId.Value))
            WinnerActivityId = null;
    }
}
