using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// Shared set-up of the old-text conversion tests (#130): one in-memory library (<see cref="LibraryHarness"/>),
/// old Activity rows seeded straight into its Activities table, and the Comment helpers.
/// </summary>
public abstract class ActivityNoteConversionTestBase
{
    private protected readonly LibraryHarness h = new();

    // An Activity row as an older version stored it, with the old single text in its old-text column
    // and the time storage last wrote it. The Activity itself no longer has that field (#133).
    private protected Guid SeedActivity(string? oldText, DateTimeOffset? lastModified = null) =>
        SeedRow(row => row[OldActivityRows.OldTextColumn] = oldText, lastModified);

    private protected Guid SeedRow(Action<TableEntity> oldColumn, DateTimeOffset? lastModified = null, Guid? gameId = null, Guid? id = null)
    {
        var row = OldActivityRows.Row(id ?? Guid.NewGuid(), gameId ?? Guid.NewGuid(), "Old activity", null);
        row.Timestamp = lastModified ?? new DateTimeOffset(2026, 9, 14, 20, 30, 0, TimeSpan.Zero);
        oldColumn(row);
        h.Activities[row.RowKey] = row;
        return Guid.Parse(row.RowKey);
    }

    private protected List<CommentEntity> CommentsOf(Guid activityId) =>
        h.StoredComments().Where(c => c.ActivityId == activityId).ToList();

    // The Comment inserts as storage answers them, except that the given insert (1 = the first) fails
    // with a storage outage first.
    private protected void FailCommentInsert(int failing)
    {
        var adds = 0;
        h.CommentTable.Setup(t => t.AddEntityAsync(It.IsAny<CommentEntity>(), It.IsAny<CancellationToken>()))
            .Returns((CommentEntity row, CancellationToken _) => ++adds == failing
                ? throw new RequestFailedException(503, "Storage unavailable")
                : Task.FromResult(TableMocks.AddRow(h.CommentRows, row)));
    }

    private protected int CommentAdds() => h.CommentTable.Invocations.Count(i => i.Method.Name == nameof(TableClient.AddEntityAsync)
        && i.Arguments[0] is CommentEntity);

    // The startup service resolves the conversion from the host's services when it runs.
    private protected ServiceProvider HostServices() =>
        new ServiceCollection().AddSingleton(h.OldTextConversion).BuildServiceProvider();
}
