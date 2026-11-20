using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Moq;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// The old-note conversion (#130): each Activity's non-empty old single note becomes that
/// Activity's first Comment, once, through the conversion class over the in-memory tables of
/// <see cref="LibraryHarness"/>.
/// </summary>
[TestClass]
public class ActivityNoteConversionTests
{
    private readonly LibraryHarness h = new();

    // An Activity row as an older version stored it, with its old note and the time storage last wrote it.
    private Guid SeedActivity(string? note, DateTimeOffset? lastModified = null)
    {
        var id = Guid.NewGuid();
        var row = new TableEntity("Activity", id.ToString("D"))
        {
            ["GameId"] = Guid.NewGuid(),
            ["Name"] = "Old activity",
            ["ThemeIds"] = "[]",
            ["HolidayIds"] = "[]",
            ["Comments"] = note,
            Timestamp = lastModified ?? new DateTimeOffset(2026, 9, 14, 20, 30, 0, TimeSpan.Zero),
        };
        h.Activities[row.RowKey] = row;
        return id;
    }

    private List<CommentEntity> CommentsOf(Guid activityId) =>
        h.Comments.Values.Where(c => c.ActivityId == activityId).ToList();

    [TestMethod]
    public async Task AnActivityWithANoteGetsOneCommentWithTheTrimmedNoteDatedByTheRowsLastWriteAndNoEvent()
    {
        var lastModified = new DateTimeOffset(2026, 8, 2, 19, 45, 12, TimeSpan.Zero);
        var activityId = SeedActivity("  Ran long, cut the second round  ", lastModified);

        await h.NoteConversion.RunAsync(default);

        var comment = Assert.ContainsSingle(CommentsOf(activityId));
        Assert.AreEqual("Ran long, cut the second round", comment.Text);
        Assert.AreEqual(lastModified, comment.CreatedUtc);
        Assert.AreEqual(TimeSpan.Zero, comment.CreatedUtc.Offset);
        Assert.IsNull(comment.EventId);
        Assert.AreEqual("Comment", comment.PartitionKey);
        Assert.AreEqual(ActivityNoteConversion.CommentIdFor(activityId), comment.Id);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   \t\r\n  ")]
    public async Task AnActivityWithNoNoteOrOnlyWhitespaceGetsNoComment(string? note)
    {
        var withNote = SeedActivity("Keep");
        var without = SeedActivity(note);

        await h.NoteConversion.RunAsync(default);

        Assert.IsEmpty(CommentsOf(without));
        Assert.ContainsSingle(CommentsOf(withNote));
    }

    [TestMethod]
    public async Task ANoteOverTheCommentLimitIsCutTo1000CharactersAndAWarningNamesTheActivity()
    {
        var note = new string('a', 999) + "bcdef";
        var activityId = SeedActivity("  " + note + "  ");
        var exact = SeedActivity(new string('x', 1000));

        await h.NoteConversion.RunAsync(default);

        Assert.AreEqual(note[..1000], Assert.ContainsSingle(CommentsOf(activityId)).Text);
        Assert.AreEqual(new string('x', 1000), Assert.ContainsSingle(CommentsOf(exact)).Text);
        var warning = Assert.ContainsSingle(h.NoteConversionLog.Entries.Where(e => e.Level == LogLevel.Warning));
        Assert.Contains(activityId.ToString("D"), warning.Message);
        Assert.DoesNotContain(exact.ToString("D"), warning.Message);
    }

    [TestMethod]
    public async Task TheCutNeverSplitsACharacterMadeOfTwoUtf16Units()
    {
        // 999 letters, then an emoji (two UTF-16 units) across the 1000th and 1001st positions.
        var activityId = SeedActivity(new string('a', 999) + "\U0001F3B2" + "tail");

        await h.NoteConversion.RunAsync(default);

        Assert.AreEqual(new string('a', 999), Assert.ContainsSingle(CommentsOf(activityId)).Text);
    }

    // ---- runs again on every host start: idempotent, and done once

    private bool MarkerStored() =>
        h.CommentTableOtherRows.ContainsKey($"{ActivityNoteConversion.MarkerPartitionKey}|{ActivityNoteConversion.MarkerRowKey}");

    private int CommentAdds() => h.CommentTable.Invocations.Count(i => i.Method.Name == nameof(TableClient.AddEntityAsync)
        && i.Arguments[0] is CommentEntity);

    [TestMethod]
    public async Task TwoRunsCreateOneCommentAndTheSecondRunWritesNothing()
    {
        var activityId = SeedActivity("Only once");

        await h.NoteConversion.RunAsync(default);
        var writesAfterFirst = h.Writes();
        await h.NoteConversion.RunAsync(default);

        Assert.ContainsSingle(CommentsOf(activityId));
        Assert.IsTrue(MarkerStored(), "A finished pass stores the marker row.");
        Assert.ContainsSingle(h.CommentTableOtherRows);
        Assert.AreEqual(writesAfterFirst, h.Writes(), "The second run must not write.");
        Assert.AreEqual(1, CommentAdds());
    }

    [TestMethod]
    public async Task AConvertedCommentTheAdminDeletedStaysDeletedOnTheNextRun()
    {
        var activityId = SeedActivity("Delete me");
        await h.NoteConversion.RunAsync(default);
        var comment = Assert.ContainsSingle(CommentsOf(activityId));

        Assert.AreEqual(204, ApiResults.Status(await h.CommentApi.Delete(TestRequests.Empty(), activityId, comment.Id, default)));
        await h.NoteConversion.RunAsync(default);

        Assert.IsEmpty(CommentsOf(activityId));
        Assert.AreEqual("Delete me", h.Activities[activityId.ToString("D")].GetString("Comments"), "The old field is not cleared.");
    }

    [TestMethod]
    public async Task APassThatFailsPartWayStoresNoMarkerAndTheRetryCompletesWithoutDuplicates()
    {
        var first = SeedActivity("First");
        var second = SeedActivity("Second");
        var third = SeedActivity("Third");
        // The second Comment insert fails with a storage outage; every other insert behaves like storage.
        var adds = 0;
        h.CommentTable.Setup(t => t.AddEntityAsync(It.IsAny<CommentEntity>(), It.IsAny<CancellationToken>()))
            .Returns((CommentEntity row, CancellationToken _) =>
            {
                if (++adds == 2) throw new RequestFailedException(503, "Storage unavailable");
                if (!h.Comments.TryAdd(row.RowKey, row))
                    throw new RequestFailedException(409, "Entity already exists", "EntityAlreadyExists", null);
                row.ETag = TableMocks.NextETag();
                return Task.FromResult<Response>(new StorageResponse(row.ETag));
            });

        await Assert.ThrowsAsync<RequestFailedException>(() => h.NoteConversion.RunAsync(default));
        Assert.IsFalse(MarkerStored(), "A pass that failed part-way must not store the marker.");
        Assert.HasCount(1, h.Comments);

        await h.NoteConversion.RunAsync(default);

        Assert.IsTrue(MarkerStored());
        foreach (var activityId in new[] { first, second, third })
            Assert.ContainsSingle(CommentsOf(activityId));
        Assert.HasCount(3, h.Comments);
    }

    [TestMethod]
    public async Task DeletingAnActivityAfterTheConversionKeepsTheMarker()
    {
        var activityId = SeedActivity("Doomed");
        await h.NoteConversion.RunAsync(default);

        Assert.IsExactInstanceOfType<Microsoft.AspNetCore.Mvc.NoContentResult>(
            await h.ActivityApi.Delete(TestRequests.Empty(), activityId, default));

        Assert.IsEmpty(h.Comments);
        Assert.IsTrue(MarkerStored(), "The Activity-delete cascade deletes Comments only, never the marker.");
    }

    // ---- read back through the Comment list route

    [TestMethod]
    public async Task TheConvertedCommentIsListedWithTheRowsDateAndNoEvent()
    {
        var lastModified = new DateTimeOffset(2026, 7, 4, 22, 0, 5, TimeSpan.Zero);
        var activityId = SeedActivity("Bring snacks next time", lastModified);
        await h.NoteConversion.RunAsync(default);

        var listed = ApiResults.Value<List<CommentEntity>>(await h.CommentApi.List(TestRequests.Empty(), activityId, default));

        var comment = Assert.ContainsSingle(listed);
        Assert.AreEqual(ActivityNoteConversion.CommentIdFor(activityId), comment.Id);
        Assert.AreEqual(activityId, comment.ActivityId);
        Assert.AreEqual("Bring snacks next time", comment.Text);
        Assert.AreEqual(lastModified, comment.CreatedUtc);
        Assert.IsNull(comment.EventId);
        Assert.AreNotEqual(default(ETag), comment.ETag);
    }

    [TestMethod]
    public async Task TheMarkerRowNeverAppearsInTheListRoute()
    {
        var activityId = SeedActivity("Note");
        await h.NoteConversion.RunAsync(default);
        Assert.IsTrue(MarkerStored());

        var listed = ApiResults.Value<List<CommentEntity>>(await h.CommentApi.List(TestRequests.Empty(), activityId, default));

        Assert.ContainsSingle(listed);
        // The list query sent to the table names the Comment partition, so a row of the marker's
        // partition is excluded by storage even if it carried the Activity's id.
        var query = h.CommentTable.Invocations.Last(i => i.Method.Name == nameof(TableClient.QueryAsync));
        var filter = Assert.IsInstanceOfType<System.Linq.Expressions.Expression<Func<CommentEntity, bool>>>(query.Arguments[0]).Compile();
        Assert.IsTrue(filter(new CommentEntity { PartitionKey = "Comment", ActivityId = activityId }));
        Assert.IsFalse(filter(new CommentEntity { PartitionKey = ActivityNoteConversion.MarkerPartitionKey, ActivityId = activityId }));
    }

    // ---- started with the host

    [TestMethod]
    public async Task TheStartupServiceRunsTheConversion()
    {
        var activityId = SeedActivity("Converted at start");
        var startup = new ActivityNoteConversionStartup(h.NoteConversion, new ListLogger<ActivityNoteConversionStartup>());

        await startup.StartAsync(default);
        await startup.ExecuteTask!;

        Assert.ContainsSingle(CommentsOf(activityId));
        Assert.IsTrue(MarkerStored());
    }

    [TestMethod]
    public async Task AFailedConversionIsLoggedAndNeverStopsTheHost()
    {
        SeedActivity("Will fail");
        var outage = new RequestFailedException(503, "Storage unavailable");
        h.CommentTable.Setup(t => t.AddEntityAsync(It.IsAny<CommentEntity>(), It.IsAny<CancellationToken>())).ThrowsAsync(outage);
        var log = new ListLogger<ActivityNoteConversionStartup>();
        var startup = new ActivityNoteConversionStartup(h.NoteConversion, log);

        await startup.StartAsync(default);
        await startup.ExecuteTask!;

        Assert.IsTrue(startup.ExecuteTask!.IsCompletedSuccessfully, "The failure must not fault the hosted service.");
        var entry = Assert.ContainsSingle(log.Entries);
        Assert.AreEqual(LogLevel.Error, entry.Level);
        Assert.AreSame(outage, entry.Exception);
        Assert.IsFalse(MarkerStored());
        await startup.StopAsync(default);
    }

    [TestMethod]
    public async Task TheConversionNeverEditsTheActivityRow()
    {
        var activityId = SeedActivity("Old note");
        var before = h.Activities[activityId.ToString("D")];

        await h.NoteConversion.RunAsync(default);

        Assert.AreSame(before, h.Activities[activityId.ToString("D")]);
        Assert.AreEqual("Old note", before.GetString("Comments"));
        Assert.IsFalse(h.ActivityTable.Invocations.Any(i => i.Method.Name is nameof(TableClient.UpsertEntityAsync)
            or nameof(TableClient.UpdateEntityAsync) or nameof(TableClient.AddEntityAsync) or nameof(TableClient.DeleteEntityAsync)));
    }
}
