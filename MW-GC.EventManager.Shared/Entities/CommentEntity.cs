namespace MW_GC.EventManager.Shared.Entities;

/// <summary>
/// One Comment on an Activity: a dated, plain-text note, stored as its own row in the
/// <c>ActivityComments</c> table with its own eTag (ADR 0001), so editing one Comment never
/// conflicts with editing the Activity or another of its Comments.
/// </summary>
public class CommentEntity : EntityBase
{
    /// <summary>The Activity the Comment is about.</summary>
    public Guid ActivityId { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>When the Comment was added, set by the API in UTC. An edit keeps it.</summary>
    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>The Event the Comment was written from, if any. Kept when that Event is deleted.</summary>
    public Guid? EventId { get; set; }
}
