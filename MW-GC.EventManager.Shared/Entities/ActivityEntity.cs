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

    // An Activity's Comments are rows of their own (CommentEntity, ADR 0001). The old single text
    // left this class in #133; ActivityNoteConversion still reads its stored column raw.
}
