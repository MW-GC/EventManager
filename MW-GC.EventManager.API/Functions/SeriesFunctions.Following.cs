using Azure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Requests;

namespace MW_GC.EventManager.API.Functions;

/// <summary>
/// <c>PUT /api/series/{id}?scope=following&amp;occurrence={eventId}</c>: ends the Series just before the chosen
/// Occurrence and starts a new Series (the body) there. The old Series records where it was split
/// (<see cref="SeriesEntity.SplitAt"/>), so a repeat of the same request finishes an edit that stopped part-way,
/// even after the chosen Occurrence has moved to the new Series.
/// </summary>
internal sealed partial class SeriesFunctions
{
    public const string OccurrenceMessage = "Occurrence must name an Occurrence of this Series.";
    public const string FollowingStartMessage = "Start must be after the Occurrence before the one this change starts from.";
    public const string FollowingFirstMessage = "This and following from the first Occurrence changes the whole Series: use scope all.";
    public const string SplitAgainMessage = "This Series was already split by a this and following edit at another Occurrence. Edit the Series that follows it, or this one with scope all.";

    /// <summary>How far before the chosen Occurrence a "this and following" edit ends the old Series.</summary>
    public static readonly TimeSpan EndBefore = TimeSpan.FromSeconds(1);

    // Null when the old Series' write lost to another write and the request named no version: read again and redo.
    private async Task<IActionResult?> EditFollowingAsync(
        HttpRequest req, SeriesEntity existing, SeriesEntity edited, ETag? pinned, DateTimeOffset now, CancellationToken ct)
    {
        if (await ChosenKeyAsync(req, existing, ct) is not { } from) return new BadRequestObjectResult(OccurrenceMessage);
        // The same split again is a repeat of an edit that may have stopped part-way: it finishes the job. A
        // split at another Occurrence would leave two Series that both follow this one.
        var repeat = existing.SplitAt == from;
        if (existing.SplitAt is not null && !repeat) return new BadRequestObjectResult(SplitAgainMessage);
        // A repeat writes nothing to the old Series, so a version it names is checked here: a stale one is a 409.
        if (repeat && pinned is { } version && version != existing.ETag) return EntityResults.Conflict();
        // The old Series would end before its own start, which no stored Series may do.
        if (from <= existing.Schedule.First) return new BadRequestObjectResult(FollowingFirstMessage);

        var occurrences = await _occurrences.OfSeriesAsync(existing.Id, ct);
        var before = Keys(occurrences).Where(k => k < from).ToList();
        if (before.Count > 0 && edited.Schedule.First <= before.Max()) return new BadRequestObjectResult(FollowingStartMessage);
        Plan(edited, now);

        if (!repeat)
        {
            // End the old Series just before the chosen Occurrence (never later than an end it already had).
            var end = from - EndBefore;
            if (existing.Schedule.End is { } oldEnd && oldEnd < end) end = oldEnd;
            existing.Schedule = existing.Schedule with { End = end };
            existing.SplitAt = from;
            var outcome = await _store.UpdateAsync(existing, pinned ?? existing.ETag, ct);
            if (outcome == UpdateOutcome.Conflict && pinned is null) return null;
            if (outcome != UpdateOutcome.Updated) return Refused(outcome);
        }

        var rewritable = await RewritableAsync(now, ct);
        var following = occurrences.Where(o => o.OccurrenceKey >= from).ToList();
        var moving = following.Where(rewritable).ToList();
        var staying = following.Except(moving).ToList();

        // The new Series has an id derived from the old one and the chosen key, so a repeated request finds
        // the Series the first one made instead of making a second. It skips the old Series' skipped keys, and
        // its own instants on the dates of the Occurrences that stay with the old Series (Modified, played or
        // past), so no run of the new Series makes a second Occurrence on one of those dates.
        edited.Id = FollowingSeriesId(existing.Id, from);
        edited.SplitAt = null;
        var skipped = Held(edited, existing, staying, now);
        edited.Skipped = skipped;
        if (!await _store.TryAddAsync(edited, ct))
        {
            var stored = await _store.GetAsync(edited.Id, ct);
            if (stored is null || Details(stored) != Details(edited)) return EntityResults.Conflict();
            // On a repeat, an Occurrence cancelled since the first request recorded its key on the old Series
            // only: the new Series takes it too, so finishing the edit never makes it again.
            edited = skipped.Except(stored.Skipped).Any()
                ? await SeriesOccurrences.RecordSkippedAsync(_store, stored.Id, skipped, ct) ?? stored
                : stored;
        }

        // An Occurrence cancelled while this ran recorded its key on the old Series; it is held here and
        // recorded on the new one too, so neither Series makes it again.
        var done = await _occurrences.ReconcileAsync(edited, moving, Keys(staying), now, ct);
        edited = await KeepHeldAsync(edited, done, ct);
        SetETag(req, existing);
        return new OkObjectResult(new SeriesChange
        {
            Series = existing, NewSeries = edited, Created = done.Created, Updated = done.Updated, Deleted = done.Deleted,
        });
    }

    /// <summary>
    /// The occurrence key of the Occurrence the request names: one of this Series' Occurrences, or the one this
    /// Series was split at, which has moved to the Series that follows (its id is derived from this Series' id
    /// and the key, so it is known without the row). Null when it names neither.
    /// </summary>
    private async Task<DateTimeOffset?> ChosenKeyAsync(HttpRequest req, SeriesEntity existing, CancellationToken ct)
    {
        if (!Guid.TryParseExact(req.Query["occurrence"].ToString(), "D", out var chosenId)) return null;
        if (await _events.GetAsync(chosenId, ct) is { OccurrenceKey: { } key } chosen && chosen.SeriesId == existing.Id) return key;
        return existing.SplitAt is { } at && chosenId == SeriesOccurrences.OccurrenceId(existing.Id, at) ? at : null;
    }

    /// <summary>
    /// The id of the Series a "this and following" edit of <paramref name="seriesId"/> at
    /// <paramref name="from"/> starts: the first 16 bytes of the SHA-256 of
    /// <c>series-following:{series id in D format}:{from as UTC ticks}</c> (UTF-8). Stored data depends on it.
    /// </summary>
    internal static Guid FollowingSeriesId(Guid seriesId, DateTimeOffset from) =>
        SeriesOccurrences.DerivedId("series-following:", seriesId, from);
}
