using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Shared.Events;

/// <summary>What the edit dialog holds when Update Event is pressed: the values a change is read from.</summary>
public sealed record OccurrenceEdits(string Name, DateTimeOffset Date, bool UniqueGames, IReadOnlyList<(Guid GameId, Guid ActivityId)> Slots);

/// <summary>What the admin changed, compared with the Occurrence as the dialog opened it.</summary>
public sealed record OccurrenceChange(bool Name, bool Day, bool Time, bool UniqueGames, bool Slots)
{
    public bool Any => Name || Day || Time || UniqueGames || Slots;

    /// <summary>
    /// A Series holds the name, the time of day and Unique games only; the day and the slots belong to each
    /// Occurrence. So a change can reach the Series only when it changed nothing else.
    /// </summary>
    public bool SeriesWide => Any && !Day && !Slots;
}

/// <summary>
/// The scope question asked before an Occurrence is updated from the edit Event dialog (#100, #204, stories 12 to 16
/// and 21), and the Series body a series scope sends. Pure: no clock, no JS, no services.
/// </summary>
public static class SeriesEdit
{
    public const string Title = "Update repeating event";
    public const string GroupLabel = "What to update";

    /// <summary>The one line shown instead of the usual message when only this event can take the change.</summary>
    public const string EventOnlyReason = "A changed date or changed activities apply to this event only.";

    /// <summary>A series scope refused before the Series is written: the new time is on another day at the Series' offset.</summary>
    public const string OtherDayMessage = "At the series' own UTC offset this time falls on another day, so only this event can take it. Choose This event only, or another time.";

    /// <summary>
    /// What changed. The day and the time of day are read at <paramref name="edits"/>' own UTC offset (the browser's,
    /// as the dialog shows them), so an unchanged date and time are never a change. Slots compare by their Game and
    /// Activity ids, in order: a roll, a replaced activity, an added or a removed slot is a change.
    /// </summary>
    public static OccurrenceChange Compare(EventEntity occurrence, OccurrenceEdits edits)
    {
        var was = occurrence.Date.ToOffset(edits.Date.Offset);
        var stored = (occurrence.Selections ?? []).Select(s => (s.Game.Id, s.Activity.Id));
        return new OccurrenceChange(
            Name: edits.Name != occurrence.Name,
            Day: was.Date != edits.Date.Date,
            Time: was.TimeOfDay != edits.Date.TimeOfDay,
            UniqueGames: edits.UniqueGames != occurrence.UniqueGamesOnly,
            Slots: !edits.Slots.SequenceEqual(stored));
    }

    /// <summary>
    /// The question about the Occurrence <paramref name="name"/> on <paramref name="date"/> (as the list shows it): all
    /// three scopes when the change can reach the Series, otherwise This event only with the one-line reason. Nothing
    /// is chosen when it opens, and it is not destructive.
    /// </summary>
    public static ScopeRequest Question(OccurrenceChange change, string name, DateTimeOffset date, Func<Task> thisEvent, Func<Task> following, Func<Task> all)
    {
        var only = new ScopeOption("This event only",
            "Only this event changes. Later changes to the series will not touch it.",
            "Update this event", thisEvent);
        if (!change.SeriesWide) return new ScopeRequest(Title, EventOnlyReason, GroupLabel, [only], destructive: false);
        return new ScopeRequest(Title, $"{name} on {date.ToLocalTime():g} is one event of a series. Choose which events take this change.", GroupLabel,
        [
            only,
            new("This and following events",
                "This event and every later one that has not been played or edited by hand take the change. Earlier events stay as they are.",
                "Update this and following", following),
            new("All future events",
                "Every future event of the series that has not been played or edited by hand takes the change.",
                "Update all future events", all),
        ], destructive: false);
    }

    /// <summary>
    /// <see cref="OtherDayMessage"/> when the new time of day, read at the stored Series' UTC offset, lands on another
    /// day than the Occurrence's own date there (only when the browser's offset differs from the Series'); else null.
    /// </summary>
    public static string? OtherDay(SeriesEntity stored, EventEntity occurrence, OccurrenceEdits edits)
    {
        if (!Compare(occurrence, edits).Time) return null;
        var offset = stored.Schedule.First.Offset;
        return edits.Date.ToOffset(offset).Date == occurrence.Date.ToOffset(offset).Date ? null : OtherDayMessage;
    }

    /// <summary>
    /// The body of a series scope: the stored Series as read, with only what the admin changed replaced: the name,
    /// Unique games only, and the time of day of the start. The new time of day is the dialog's instant read at the
    /// stored start's UTC offset, and the start keeps that offset (the Series' calendar arithmetic runs at it).
    /// <see cref="SeriesScope.All"/> keeps the stored start's day; <see cref="SeriesScope.Following"/> starts on the day of
    /// the chosen Occurrence's key (the instant the Series made it for, its date when no key was read) at that offset:
    /// the API starts the new Series there and refuses one that starts on or before an earlier Occurrence. The rule, the
    /// end, the slot count, the Filters, <c>skipped</c>, <c>splitAt</c>, <c>splitFrom</c> and the eTag go back as read.
    /// </summary>
    public static SeriesEntity Body(SeriesEntity stored, EventEntity occurrence, OccurrenceEdits edits, SeriesScope scope)
    {
        var change = Compare(occurrence, edits);
        var first = stored.Schedule.First;
        var time = change.Time ? edits.Date.ToOffset(first.Offset).TimeOfDay : first.TimeOfDay;
        var day = scope == SeriesScope.Following ? (occurrence.OccurrenceKey ?? occurrence.Date).ToOffset(first.Offset).Date : first.Date;
        return new SeriesEntity
        {
            Id = stored.Id,
            PartitionKey = stored.PartitionKey,
            Timestamp = stored.Timestamp,
            ETag = stored.ETag,
            Name = change.Name ? edits.Name : stored.Name,
            Schedule = stored.Schedule with { First = new DateTimeOffset(day + time, first.Offset) },
            SlotCount = stored.SlotCount,
            UniqueGamesOnly = change.UniqueGames ? edits.UniqueGames : stored.UniqueGamesOnly,
            Filters = stored.Filters,
            Skipped = stored.Skipped,
            SplitAt = stored.SplitAt,
            SplitFrom = stored.SplitFrom,
        };
    }
}
