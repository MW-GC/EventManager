namespace MW_GC.EventManager.Shared.Models;

/// <summary>
/// One Event's Event metrics with its Like count joined in, as <c>GET /api/event-metrics</c> answers
/// for every Event that has a stored metrics row or at least one Like (#136). An Event with neither
/// has no item: read that as no metrics and 0 Likes. The metric fields are null when not recorded.
/// </summary>
public sealed record EventMetricsWithLikes(
    Guid EventId, int? Attendees, int? NewAttendees, int? ReturningAttendees, decimal? AmountSpent, int Likes);
