using MW_GC.EventManager.Shared.Models;

namespace MW_GC.EventManager.Shared.Entities;

public class ActivityEntity : EntityBase
{
    public Guid GameId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Rules { get; set; } = string.Empty;
    public List<Guid> ThemeIds { get; set; } = [];
    public List<Guid> HolidayIds { get; set; } = [];
    public string SetupRequirements { get; set; } = string.Empty;

    // Players and duration (#98), each optional (null is not set). A row stored before they existed has no
    // such columns and reads back with all three null: no migration.
    public int? MinPlayers { get; set; }
    public int? MaxPlayers { get; set; }
    public int? DurationMinutes { get; set; }

    // An Activity's Comments are rows of their own (CommentEntity, ADR 0001). The old single text
    // left this class in #133; ActivityNoteConversion still reads its stored column raw.
}
