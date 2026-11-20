namespace MW_GC.EventManager.Shared.Entities;

/// <summary>
/// The Event metrics of one Event: the optional numbers recorded after it was played. Stored as
/// its own row in the <c>EventMetrics</c> table, apart from the Event (ADR 0001), so an Event save
/// that rewrites its Selections can never change them. The row key is the Event id, so
/// <see cref="EntityBase.Id"/> is the Event id. Every field is optional; null means not recorded.
/// Adding a metric is one change here, one rule in <c>EventMetricsValidator</c> and one form field.
/// </summary>
public class EventMetricsEntity : EntityBase
{
    public int? Attendees { get; set; }
    public int? NewAttendees { get; set; }
    public int? ReturningAttendees { get; set; }

    /// <summary>Not a Table Storage native type: stored as a JSON number in a string property.</summary>
    public decimal? AmountSpent { get; set; }
}
