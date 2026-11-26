using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Web.Services;

/// <summary>
/// The Activities the avoid-recent Filter (#95) skips: every Activity offered in the last <c>count</c> Events dated
/// before the Event being planned, not counting the Event being edited. An Event counts once however many of its
/// Selections share an Activity, and an Event with no Selections (an Occurrence not yet rolled) offered nothing, so
/// it does not use up one of the <c>count</c> places. Plain inputs only: the dialog hands in its Events list, the
/// instant it is planning for and the edited Event's id, and gives the planner the ids this returns.
/// </summary>
public static class RecentActivities
{
    /// <summary>The largest number of recent Events the Filter may look back over: Shared's limit, which the API checks a
    /// Series' avoid recent against (#232), so the box and the API never disagree.</summary>
    public const int MaxCount = SeriesFilters.MaxAvoidRecent;

    public static IReadOnlySet<Guid> Ids(IEnumerable<EventEntity> events, DateTimeOffset planned, Guid? editing, int count) =>
        events
            .Where(e => e.Id != editing && e.Date < planned && e.Selections is { Count: > 0 })
            .OrderByDescending(e => e.Date)
            .Take(count)
            .SelectMany(e => e.Selections.Select(s => s.Activity.Id))
            .ToHashSet();
}
