using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.API.Services;

/// <summary>
/// What one daily run did: how many Series rows it saw, how many Occurrences it created, and the row keys of the
/// Series it could not extend. A Series that failed part-way counts none of the Occurrences it created before
/// the failure; the next run sees them as stored and does not make them again.
/// </summary>
internal sealed record SeriesExtension(int Seen, int Created, IReadOnlyList<string> Failed);

/// <summary>
/// The daily run (#204, ADR 0002): extends every Series to the horizon from the same instant, with the same
/// call a Series create makes. Each Series' stored Occurrences are all kept (none is a candidate), so the run
/// only adds the ones the moving horizon now reaches, never rewrites, moves or deletes one, never makes a
/// cancelled key again, and never writes the Series row. Occurrence ids are derived from the Series and the
/// key and created with the atomic add, so a second or overlapping run, or one racing a create, makes no twin
/// and overwrites nothing. One Series that fails (a read or write error, or a row that cannot be read) is
/// logged with its id and the run goes on with the others.
/// </summary>
internal sealed class SeriesExtender(
    TableServiceClient service,
    TableStore<SeriesEntity> store,
    SeriesOccurrences occurrences,
    ILogger<SeriesExtender> logger)
{
    /// <summary>
    /// Extends every Series from <paramref name="clock"/>'s now, read once for the whole run. Never throws for
    /// one Series; a cancellation stops the run and is thrown.
    /// </summary>
    public async Task<SeriesExtension> ExtendAllAsync(TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var keys = await SeriesKeysAsync(ct);
        var created = 0;
        var failed = new List<string>();

        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                created += await ExtendAsync(key, now, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Extending Series {SeriesId} failed; the next daily run tries it again.", key);
                failed.Add(key);
            }
        }

        if (failed.Count == 0)
            logger.LogInformation("Series extension: {Seen} Series seen, {Created} Occurrences created, 0 Series failed.", keys.Count, created);
        else
            logger.LogError("Series extension: {Seen} Series seen, {Created} Occurrences created, {Failed} Series failed: {FailedIds}.",
                keys.Count, created, failed.Count, string.Join(", ", failed));
        return new SeriesExtension(keys.Count, created, failed);
    }

    // One Series: read it whole (a row that cannot be read fails this Series only), keep every stored
    // Occurrence and let the create's reconcile add what the horizon now needs. A Series deleted since the
    // listing has nothing to extend.
    private async Task<int> ExtendAsync(string key, DateTimeOffset now, CancellationToken ct)
    {
        if (!Guid.TryParseExact(key, "D", out var id)) throw new FormatException($"The Series row key '{key}' is not an id.");
        // Occurrences are read before the Series: a cancel records the key on the Series and then deletes the row,
        // so a cancel in flight is either in Skipped or still in kept, and is never made again.
        var kept = (await occurrences.OfSeriesAsync(id, ct))
            .Where(o => o.OccurrenceKey is not null).Select(o => o.OccurrenceKey!.Value).ToList();
        if (await store.GetAsync(id, ct) is not { } series) return 0;
        return (await occurrences.ReconcileAsync(series, [], kept, now, ct)).Created;
    }

    // The row keys of every Series, without reading the rows into Series, so one malformed row cannot stop the
    // listing of the others.
    private async Task<List<string>> SeriesKeysAsync(CancellationToken ct)
    {
        var table = service.GetTableClient(TableNames.Series);
        var keys = new List<string>();
        await foreach (var row in table.QueryAsync<TableEntity>(e => e.PartitionKey == TableNames.SeriesPartition,
                           select: [nameof(TableEntity.RowKey)], cancellationToken: ct))
            keys.Add(row.RowKey);
        return keys;
    }
}
