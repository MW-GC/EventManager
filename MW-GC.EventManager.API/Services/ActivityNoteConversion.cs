using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.API.Services;

/// <summary>
/// Turns each Activity's old single text into that Activity's first Comment (#130, ADR 0001).
/// The Activity no longer has that field (#133), so the text is read from the old
/// <c>Comments</c> column of the raw stored Activity row; a row without that column, or with a
/// value that is not a string, has nothing to convert. A non-empty text (after trimming) becomes
/// one Comment with no Event, dated by the Activity row's last write (its <c>Timestamp</c>), and
/// cut to at most <see cref="FieldRules.CommentTextMaxLength"/> characters and trimmed again only
/// when it is longer (with a warning naming the Activity). The Activity row is only read, never written.
/// <para>
/// It runs on every host start (<see cref="ActivityNoteConversionStartup"/>) and is safe to
/// repeat: each Comment's id comes from its Activity's id and is inserted, never replaced, so a
/// second pass adds nothing. When a pass finishes it stores one marker row in the
/// <c>ActivityComments</c> table under its own partition key, and a later run that finds the
/// marker does nothing, so a converted Comment the admin deleted never comes back. A pass that
/// fails part-way stores no marker, and the next start finishes it.
/// </para>
/// <para>
/// A later Activity <c>PUT</c> replaces the row through the typed entity and so drops the old
/// column; that is intended once the marker is stored. This class, its startup service and the
/// marker check can be deleted once every deployment has stored the marker.
/// </para>
/// </summary>
internal sealed class ActivityNoteConversion
{
    /// <summary>The marker's partition in <c>ActivityComments</c>: never the Comment partition, so no Comment read or delete sees it.</summary>
    public const string MarkerPartitionKey = "Migration";

    /// <summary>The marker's row key. A later conversion of the same table would use a new one.</summary>
    public const string MarkerRowKey = "activity-notes-v1";

    private const string CommentTable = "ActivityComments";

    // The table and partition the Activity TableStore uses (Program.cs).
    private const string ActivityTable = "Activities";
    private const string ActivityPartitionKey = "Activity";

    // The old stored column that held the Activity's single text.
    private const string OldTextColumn = "Comments";

    private readonly TableStore<CommentEntity> _comments;
    private readonly TableClient _activityTable;
    private readonly TableClient _commentTable;
    private readonly ILogger<ActivityNoteConversion> _logger;

    // The Activity store is taken, though not used, so that building the conversion creates the
    // Activity table the way the store does before the raw read below.
    public ActivityNoteConversion(
        TableStore<ActivityEntity> activities,
        TableStore<CommentEntity> comments,
        TableServiceClient service,
        ILogger<ActivityNoteConversion> logger)
    {
        _ = activities;
        _comments = comments;
        // The Activity rows are read raw: the old column is no longer part of ActivityEntity.
        _activityTable = service.GetTableClient(ActivityTable);
        // The Comment store creates the table; the marker goes through the raw client because it
        // is not a Comment and does not live in the Comment partition.
        _commentTable = service.GetTableClient(CommentTable);
        _logger = logger;
    }

    /// <summary>The id of the Comment converted from an Activity's old note: the same on every run.</summary>
    public static Guid CommentIdFor(Guid activityId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("activity-note:" + activityId.ToString("D")));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// Converts every old note that has no Comment yet, then stores the marker. Does nothing when
    /// the marker is already stored. A storage failure is thrown to the caller and no marker is stored.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (await MarkerStoredAsync(ct))
        {
            _logger.LogDebug("Old Activity notes were already converted; nothing to do.");
            return;
        }

        var added = 0;
        await foreach (var row in _activityTable.QueryAsync<TableEntity>(e => e.PartitionKey == ActivityPartitionKey, cancellationToken: ct))
        {
            // Only a string in the old column is a text to convert; a missing column or another type is skipped.
            if (!row.TryGetValue(OldTextColumn, out var value) || value is not string text) continue;
            var note = text.Trim();
            if (note.Length == 0) continue;
            var activityId = Guid.Parse(row.RowKey);

            // A note longer than the Comment limit is cut, never dropped, and never in the middle
            // of a character made of two UTF-16 units.
            if (note.Length > FieldRules.CommentTextMaxLength)
            {
                var length = FieldRules.CommentTextMaxLength;
                if (char.IsHighSurrogate(note[length - 1])) length--;
                // The cut can end in whitespace; a Comment's Text is stored trimmed, so trim again.
                var kept = note[..length].Trim();
                _logger.LogWarning(
                    "The old note of Activity {ActivityId} is {Length} characters; its Comment keeps the first {Kept}.",
                    activityId, note.Length, kept.Length);
                note = kept;
                if (note.Length == 0) continue;
            }

            var comment = new CommentEntity
            {
                Id = CommentIdFor(activityId),
                ActivityId = activityId,
                Text = note,
                CreatedUtc = (row.Timestamp ?? DateTimeOffset.UtcNow).ToUniversalTime(),
                EventId = null,
            };
            // Insert only: a Comment an earlier, unfinished pass already added is left as it is.
            if (await _comments.TryAddAsync(comment, ct)) added++;
        }

        await StoreMarkerAsync(ct);
        _logger.LogInformation("Converted {Count} old Activity notes into Comments.", added);
    }

    private async Task<bool> MarkerStoredAsync(CancellationToken ct)
    {
        try
        {
            await _commentTable.GetEntityAsync<TableEntity>(MarkerPartitionKey, MarkerRowKey, cancellationToken: ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    private async Task StoreMarkerAsync(CancellationToken ct)
    {
        var marker = new TableEntity(MarkerPartitionKey, MarkerRowKey) { ["CompletedUtc"] = DateTimeOffset.UtcNow };
        try
        {
            await _commentTable.AddEntityAsync(marker, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 409 && ex.ErrorCode == "EntityAlreadyExists")
        {
            // Another instance finished the same pass first.
        }
    }
}
