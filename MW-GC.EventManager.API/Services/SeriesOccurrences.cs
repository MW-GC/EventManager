using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Azure;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Series;

namespace MW_GC.EventManager.API.Services;

/// <summary>
/// What <see cref="SeriesOccurrences.ReconcileAsync"/> did: Occurrences created, rewritten and deleted, and the
/// keys of the ones it left alone because they changed or went since they were read.
/// </summary>
internal sealed record Reconciled(int Created, int Updated, int Deleted, IReadOnlyList<DateTimeOffset> Held);

/// <summary>
/// The Occurrences of a Series (#204, ADR 0002): ordinary Event rows that carry the Series id, their
/// occurrence key and the Modified flag. Works out which to create, rewrite or delete with the pure
/// <see cref="OccurrenceGenerator"/>, and never rewrites or deletes one that is Modified, played or past
/// (the caller decides which rows may change and passes only those as candidates).
/// </summary>
internal sealed class SeriesOccurrences(
    TableStore<EventEntity> events,
    TableStore<CommentEntity> comments,
    TableStore<EventMetricsEntity> metrics)
{
    /// <summary>Text hashed for an Occurrence id; part of stored data, never change it.</summary>
    internal const string IdPrefix = "series-occurrence:";

    /// <summary>
    /// The id of the Occurrence of <paramref name="seriesId"/> at <paramref name="occurrenceKey"/>, the same on
    /// every run, so a retried or overlapping generation inserts the same row and the atomic add refuses the
    /// second one. Derivation (stored data depends on it; it must never change): the UTF-8 bytes of
    /// <c>series-occurrence:{series id in D format}:{occurrence key as UTC ticks, invariant digits}</c>,
    /// hashed with SHA-256, and the first 16 bytes of the hash passed to <c>new Guid(ReadOnlySpan&lt;byte&gt;)</c>
    /// (the same shape as the old-text conversion's Comment id, with its own prefix). UTC ticks make the
    /// same instant written at another offset the same Occurrence.
    /// </summary>
    public static Guid OccurrenceId(Guid seriesId, DateTimeOffset occurrenceKey) => DerivedId(IdPrefix, seriesId, occurrenceKey);

    /// <summary>
    /// The first 16 bytes of the SHA-256 of the UTF-8 text <c>{prefix}{series id in D format}:{instant as UTC
    /// ticks, invariant digits}</c>, read by <c>new Guid(ReadOnlySpan&lt;byte&gt;)</c>. Stored data depends on it.
    /// </summary>
    internal static Guid DerivedId(string prefix, Guid seriesId, DateTimeOffset instant)
    {
        var text = prefix + seriesId.ToString("D") + ":" + instant.UtcTicks.ToString(CultureInfo.InvariantCulture);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>The Occurrences of one Series, as stored now.</summary>
    public async Task<List<EventEntity>> OfSeriesAsync(Guid seriesId, CancellationToken ct) =>
        (await events.GetAllAsync(ct)).Where(e => e.SeriesId == seriesId).ToList();

    /// <summary>
    /// Whether an Event is played: it has a Winner, at least one Comment written from it, or Event metrics (a
    /// stored metrics row with at least one value; a row whose values were all cleared records nothing).
    /// Reads the Comments and the Event metrics once, through their own stores.
    /// </summary>
    public async Task<Func<EventEntity, bool>> PlayedAsync(CancellationToken ct)
    {
        var commented = (await comments.GetAllAsync(ct)).Where(c => c.EventId is not null).Select(c => c.EventId!.Value).ToHashSet();
        var measured = (await metrics.GetAllAsync(ct)).Where(HasValues).Select(m => m.Id).ToHashSet();
        return e => e.WinnerActivityId is not null || commented.Contains(e.Id) || measured.Contains(e.Id);
    }

    /// <summary>
    /// Makes the Occurrences of <paramref name="series"/> match its rule from <paramref name="now"/> on.
    /// <paramref name="candidates"/> are the stored Occurrences a Series change may rewrite or delete (future,
    /// not Modified, not played): one of this Series whose key the rule still makes is rewritten in place
    /// (name, date, Unique games only); every other candidate is deleted. Then every key the rule makes that
    /// no candidate kept is created. <paramref name="kept"/> are the keys of the Occurrences that stay as they
    /// are, so none is made twice. Each rewrite and delete is conditional on the row's eTag as it was read,
    /// so a row changed meanwhile is left alone, and a row gone meanwhile (an Occurrence cancelled while this
    /// ran) is not made again: the keys of both come back as <c>Held</c>, so the caller can record them as
    /// skipped on <paramref name="series"/>.
    /// </summary>
    public async Task<Reconciled> ReconcileAsync(
        SeriesEntity series, IReadOnlyCollection<EventEntity> candidates, IReadOnlyCollection<DateTimeOffset> kept,
        DateTimeOffset now, CancellationToken ct)
    {
        var keys = OccurrenceGenerator.Generate(series.Schedule, now, kept, series.Skipped);
        var wanted = keys.ToHashSet();
        var covered = new HashSet<DateTimeOffset>();
        var held = new List<DateTimeOffset>();
        int created = 0, updated = 0, deleted = 0;

        foreach (var occurrence in candidates)
        {
            if (occurrence.SeriesId == series.Id && occurrence.OccurrenceKey is { } key && wanted.Contains(key) && covered.Add(key))
            {
                if (Rewrite(occurrence, series, key) && await events.UpdateAsync(occurrence, occurrence.ETag, ct) == UpdateOutcome.Updated)
                    updated++;
                continue;
            }

            if (await DeleteOneAsync(occurrence, ct))
                deleted++;
            else if (occurrence.OccurrenceKey is { } lost)
                held.Add(lost); // changed or cancelled since it was read: it stays, or stays gone
        }

        // Nothing is made on the date of a row left alone: not its key, and not the rule's instant that day.
        held.AddRange(Occupied(series.Schedule, now, held, series.Skipped));
        covered.UnionWith(held);
        foreach (var key in keys.Where(k => !covered.Contains(k)))
            if (await events.TryAddAsync(NewOccurrence(series, key), ct))
                created++;

        return new Reconciled(created, updated, deleted, held.Distinct().ToList());
    }

    /// <summary>
    /// The instants the edited <paramref name="schedule"/> would make from <paramref name="now"/> on that fall
    /// on the calendar date (at the Series' offset) of a held key: an Occurrence that stays as it is after the
    /// edit (Modified, played or past), or a cancelled one. The edit records them as skipped, so the new rule
    /// never makes a second Occurrence on a date one already holds, and a cancelled date stays cancelled even
    /// when the edit only moves the time of day.
    /// </summary>
    public static IEnumerable<DateTimeOffset> Occupied(
        RecurrenceSeries schedule, DateTimeOffset now, IEnumerable<DateTimeOffset> held, IReadOnlyCollection<DateTimeOffset> skipped)
    {
        var offset = schedule.First.Offset;
        var dates = held.Select(k => DateOnly.FromDateTime(k.ToOffset(offset).DateTime)).ToHashSet();
        if (dates.Count == 0) return [];
        return OccurrenceGenerator.Generate(schedule, now, [], skipped)
            .Where(k => dates.Contains(DateOnly.FromDateTime(k.ToOffset(offset).DateTime)));
    }

    /// <summary>Deletes the Occurrences given, each only if unchanged since it was read; answers how many went.</summary>
    public async Task<int> DeleteAsync(IEnumerable<EventEntity> occurrences, CancellationToken ct)
    {
        var deleted = 0;
        foreach (var occurrence in occurrences)
            if (await DeleteOneAsync(occurrence, ct))
                deleted++;
        return deleted;
    }

    // Deletes an Occurrence if it is unchanged since it was read, and then its Event metrics row, as the Event
    // delete does (ADR 0001). The metrics row is read first: one that has gained a value since the played check
    // keeps the Occurrence, and the row is deleted only as it was read, so a metrics save racing this delete
    // is never lost. False when nothing was deleted.
    private async Task<bool> DeleteOneAsync(EventEntity occurrence, CancellationToken ct)
    {
        var row = await metrics.GetAsync(occurrence.Id, ct);
        if (row is not null && HasValues(row)) return false;
        if (await events.DeleteAsync(occurrence.Id, occurrence.ETag, ct) != DeleteOutcome.Deleted) return false;
        if (row is not null) await metrics.DeleteAsync(occurrence.Id, row.ETag, ct);
        return true;
    }

    private static bool HasValues(EventMetricsEntity m) =>
        m.Attendees is not null || m.NewAttendees is not null || m.ReturningAttendees is not null || m.AmountSpent is not null;

    /// <summary>
    /// Records keys as skipped on a Series (a cancelled Occurrence's, or ones a Series edit left alone), so the
    /// generator never makes them again, and answers the Series as stored afterwards (null when it is gone).
    /// The write is conditional on the Series as read, and re-reads and retries when another write changed
    /// the Series meanwhile, so a Series edit and a cancel never drop each other's change.
    /// </summary>
    public static async Task<SeriesEntity?> RecordSkippedAsync(
        TableStore<SeriesEntity> store, Guid seriesId, IReadOnlyCollection<DateTimeOffset> keys, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var series = await store.GetAsync(seriesId, ct);
            if (series is null) return null;
            var missing = keys.Where(k => !series.Skipped.Contains(k)).Distinct().ToList();
            if (missing.Count == 0) return series;
            series.Skipped.AddRange(missing);
            switch (await store.UpdateAsync(series, series.ETag, ct))
            {
                case UpdateOutcome.Updated: return series;
                case UpdateOutcome.NotFound: return null;
            }
        }

        // Five writers in a row beat this one: answer as any other storage conflict (409).
        throw new RequestFailedException(412, "The Series kept changing while a cancelled Occurrence was recorded.", "UpdateConditionNotSatisfied", null);
    }

    /// <summary>A new Occurrence of <paramref name="series"/> at <paramref name="key"/>: its name, no Selections yet.</summary>
    public static EventEntity NewOccurrence(SeriesEntity series, DateTimeOffset key) => new()
    {
        Id = OccurrenceId(series.Id, key),
        Name = series.Name,
        Date = key,
        Selections = [],
        UniqueGamesOnly = series.UniqueGamesOnly,
        SeriesId = series.Id,
        OccurrenceKey = key,
    };

    // The Series fields an Occurrence copies. True when anything changed.
    private static bool Rewrite(EventEntity occurrence, SeriesEntity series, DateTimeOffset key)
    {
        if (occurrence.Name == series.Name && occurrence.Date == key && occurrence.UniqueGamesOnly == series.UniqueGamesOnly)
            return false;
        occurrence.Name = series.Name;
        occurrence.Date = key;
        occurrence.UniqueGamesOnly = series.UniqueGamesOnly;
        return true;
    }
}
