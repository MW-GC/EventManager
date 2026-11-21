using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// The old-text conversion (#130): each Activity's non-empty old single text becomes that
/// Activity's first Comment, once, through the conversion class over the in-memory tables of
/// <see cref="LibraryHarness"/>.
/// </summary>
[TestClass]
public class ActivityNoteConversionTests
{
    private readonly LibraryHarness h = new();

    // An Activity row as an older version stored it, with the old single text in its raw Comments
    // column and the time storage last wrote it. The Activity itself no longer has that field (#133).
    private Guid SeedActivity(string? oldText, DateTimeOffset? lastModified = null) =>
        SeedRow(row => row["Comments"] = oldText, lastModified);

    private Guid SeedRow(Action<TableEntity> oldColumn, DateTimeOffset? lastModified = null, Guid? gameId = null, Guid? id = null)
    {
        var row = OldActivityRows.Row(id ?? Guid.NewGuid(), gameId ?? Guid.NewGuid(), "Old activity", null);
        row.Timestamp = lastModified ?? new DateTimeOffset(2026, 9, 14, 20, 30, 0, TimeSpan.Zero);
        oldColumn(row);
        h.Activities[row.RowKey] = row;
        return Guid.Parse(row.RowKey);
    }

    private List<CommentEntity> CommentsOf(Guid activityId) =>
        h.StoredComments().Where(c => c.ActivityId == activityId).ToList();

    // The Comment inserts as storage answers them, except that the given insert (1 = the first) fails
    // with a storage outage first.
    private void FailCommentInsert(int failing)
    {
        var adds = 0;
        h.CommentTable.Setup(t => t.AddEntityAsync(It.IsAny<CommentEntity>(), It.IsAny<CancellationToken>()))
            .Returns((CommentEntity row, CancellationToken _) => ++adds == failing
                ? throw new RequestFailedException(503, "Storage unavailable")
                : Task.FromResult(TableMocks.AddRow(h.CommentRows, row)));
    }

    [TestMethod]
    public async Task AnActivityWithAnOldTextGetsOneCommentWithTheTrimmedTextDatedByTheRowsLastWriteAndNoEvent()
    {
        var lastModified = new DateTimeOffset(2026, 8, 2, 19, 45, 12, TimeSpan.Zero);
        var activityId = SeedRow(row => row["Comments"] = "  Ran long, cut the second round  ", lastModified,
            id: OldActivityRows.FixedActivityId);

        await h.NoteConversion.RunAsync(default);

        var comment = Assert.ContainsSingle(CommentsOf(activityId));
        Assert.AreEqual("Ran long, cut the second round", comment.Text);
        Assert.AreEqual(lastModified, comment.CreatedUtc);
        Assert.AreEqual(TimeSpan.Zero, comment.CreatedUtc.Offset);
        Assert.IsNull(comment.EventId);
        Assert.AreEqual(TableNames.CommentPartition, comment.PartitionKey);
        Assert.AreEqual(OldActivityRows.FixedActivityCommentId, comment.Id);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   \t\r\n  ")]
    public async Task AnActivityWithNoOldTextOrOnlyWhitespaceGetsNoComment(string? oldText)
    {
        var withText = SeedActivity("Keep");
        var without = SeedActivity(oldText);

        await h.NoteConversion.RunAsync(default);

        Assert.IsEmpty(CommentsOf(without));
        Assert.ContainsSingle(CommentsOf(withText));
    }

    [TestMethod]
    public async Task ARowWithoutTheOldCommentsColumnGetsNoComment()
    {
        var withText = SeedActivity("Keep");
        var without = SeedRow(_ => { });
        Assert.IsFalse(h.Activities[without.ToString("D")].ContainsKey("Comments"));

        await h.NoteConversion.RunAsync(default);

        Assert.IsEmpty(CommentsOf(without));
        Assert.ContainsSingle(CommentsOf(withText));
        Assert.IsTrue(h.MarkerStored());
    }

    [TestMethod]
    public async Task ARowWhoseOldCommentsColumnIsNotAStringIsSkippedWithoutThrowing()
    {
        var withText = SeedActivity("Keep");
        // Table Storage keeps a column's type per row; this one was stored as a number.
        var odd = SeedRow(row => row["Comments"] = 42);

        await h.NoteConversion.RunAsync(default);

        Assert.IsEmpty(CommentsOf(odd));
        Assert.ContainsSingle(CommentsOf(withText));
        Assert.IsTrue(h.MarkerStored(), "The pass finishes and stores the marker.");
    }

    [TestMethod]
    public async Task AnActivityPutAfterTheConversionNeitherBringsBackNorNeedsTheOldColumn()
    {
        var game = h.SeedGame();
        var activityId = SeedRow(row => row["Comments"] = "Old text", gameId: game.Id);
        await h.NoteConversion.RunAsync(default);
        var convertedId = Assert.ContainsSingle(CommentsOf(activityId)).Id;

        // A client that still sends the old field, and one that never heard of it.
        var withOld = await h.ActivityApi.Update(
            TestRequests.Json(new { gameId = game.Id, name = "Renamed", comments = "Sneaked back in" }), activityId, default);
        Assert.AreEqual(200, ApiResults.Status(withOld));
        Assert.IsFalse(h.Activities[activityId.ToString("D")].ContainsKey("Comments"), "The PUT must not store the old column.");
        var without = await h.ActivityApi.Update(TestRequests.Json(new { gameId = game.Id, name = "Renamed again" }), activityId, default);
        Assert.AreEqual(200, ApiResults.Status(without));
        Assert.IsFalse(h.Activities[activityId.ToString("D")].ContainsKey("Comments"));
        Assert.AreEqual("Renamed again", h.Activities[activityId.ToString("D")].GetString("Name"));

        // The Comment the conversion made is kept, read again after the PUTs, and a later start adds nothing.
        await h.NoteConversion.RunAsync(default);
        var listed = ApiResults.Value<List<CommentEntity>>(await h.CommentApi.List(TestRequests.Empty(), activityId, default));
        var kept = Assert.ContainsSingle(listed);
        Assert.AreEqual(convertedId, kept.Id);
        Assert.AreEqual("Old text", kept.Text);
    }

    // ---- an Activity PUT never replaces a row whose old text is not converted yet (#133)

    [TestMethod]
    public async Task APutBeforeAnyPassKeepsTheOldTextAsACommentAndASecondPutAddsNone()
    {
        var game = h.SeedGame();
        var lastModified = new DateTimeOffset(2026, 6, 1, 18, 0, 0, TimeSpan.Zero);
        var activityId = SeedRow(row => row["Comments"] = "  Old text  ", lastModified, game.Id);
        Assert.IsFalse(h.MarkerStored(), "No pass has run: the startup service never started.");

        var first = await h.ActivityApi.Update(TestRequests.Json(new { gameId = game.Id, name = "Renamed" }), activityId, default);

        Assert.AreEqual(200, ApiResults.Status(first));
        var row = h.Activities[activityId.ToString("D")];
        Assert.AreEqual("Renamed", row.GetString("Name"));
        Assert.IsFalse(row.ContainsKey("Comments"), "The PUT did replace the row.");
        var listed = ApiResults.Value<List<CommentEntity>>(await h.CommentApi.List(TestRequests.Empty(), activityId, default));
        var comment = Assert.ContainsSingle(listed);
        Assert.AreEqual("Old text", comment.Text);
        Assert.AreEqual(activityId, comment.ActivityId);
        Assert.AreEqual(lastModified, comment.CreatedUtc);
        Assert.IsNull(comment.EventId);
        Assert.IsTrue(h.MarkerStored());

        var second = await h.ActivityApi.Update(TestRequests.Json(new { gameId = game.Id, name = "Renamed again" }), activityId, default);

        Assert.AreEqual(200, ApiResults.Status(second));
        var after = ApiResults.Value<List<CommentEntity>>(await h.CommentApi.List(TestRequests.Empty(), activityId, default));
        Assert.AreEqual(comment.Id, Assert.ContainsSingle(after).Id);
        Assert.AreEqual(1, CommentAdds());
    }

    [TestMethod]
    public async Task APutWhoseConversionFailsIs503WritesNothingAndALaterPutKeepsTheText()
    {
        var game = h.SeedGame();
        var activityId = SeedRow(row => row["Comments"] = "Old text", gameId: game.Id);
        var before = h.Activities[activityId.ToString("D")];
        // The first Comment insert fails with a storage outage; later inserts behave like storage.
        FailCommentInsert(1);

        var failed = await h.ActivityApi.Update(TestRequests.Json(new { gameId = game.Id, name = "Renamed" }), activityId, default);

        Assert.AreEqual(503, ApiResults.Status(failed));
        Assert.AreEqual("Storage is temporarily unavailable. Try again shortly.", ApiResults.Message(failed));
        Assert.AreSame(before, h.Activities[activityId.ToString("D")], "The row must not be replaced.");
        Assert.AreEqual("Old text", before.GetString("Comments"));
        Assert.AreEqual("Old activity", before.GetString("Name"));
        Assert.IsFalse(h.ActivityTable.Invocations.Any(i => i.Method.Name == nameof(TableClient.UpdateEntityAsync)));
        Assert.IsEmpty(h.StoredComments());
        Assert.IsFalse(h.MarkerStored());

        var retried = await h.ActivityApi.Update(TestRequests.Json(new { gameId = game.Id, name = "Renamed" }), activityId, default);

        Assert.AreEqual(200, ApiResults.Status(retried));
        Assert.AreEqual("Renamed", h.Activities[activityId.ToString("D")].GetString("Name"));
        var listed = ApiResults.Value<List<CommentEntity>>(await h.CommentApi.List(TestRequests.Empty(), activityId, default));
        Assert.AreEqual("Old text", Assert.ContainsSingle(listed).Text);
        Assert.IsTrue(h.MarkerStored());
    }

    [TestMethod]
    public async Task WithTheMarkerStoredAPutReadsOnlyTheMarkerOnceAndWritesTheActivityAsBefore()
    {
        // A deployment whose pass finished earlier: the marker is stored, and this Activity has no old text.
        h.SeedMarker();
        var game = h.SeedGame();
        var activityId = SeedRow(_ => { }, gameId: game.Id);

        var first = await h.ActivityApi.Update(
            TestRequests.Json(new { gameId = game.Id, name = "Renamed", rules = "Best of three" }), activityId, default);
        var second = await h.ActivityApi.Update(TestRequests.Json(new { gameId = game.Id, name = "Renamed again" }), activityId, default);

        Assert.AreEqual(200, ApiResults.Status(first));
        Assert.AreEqual(200, ApiResults.Status(second));
        var row = h.Activities[activityId.ToString("D")];
        Assert.AreEqual("Renamed again", row.GetString("Name"));
        Assert.IsFalse(row.ContainsKey("Comments"));
        // One marker read for both PUTs, no scan of the Activity table, no Comment.
        Assert.AreEqual(1, h.CommentTable.Invocations.Count(i => i.Method.Name == nameof(TableClient.GetEntityAsync)));
        Assert.AreEqual(0, h.ActivityTable.Invocations.Count(i => i.Method.Name == nameof(TableClient.QueryAsync)));
        Assert.IsEmpty(h.StoredComments());
        Assert.AreEqual(0, CommentAdds());
    }

    [TestMethod]
    public async Task APutDuringTheStartupPassWaitsForItAndTheTextIsConvertedOnce()
    {
        var game = h.SeedGame();
        var activityId = SeedRow(row => row["Comments"] = "Old text", gameId: game.Id);
        // The startup pass is held inside its Comment insert until the test lets it go.
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        h.CommentTable.Setup(t => t.AddEntityAsync(It.IsAny<CommentEntity>(), It.IsAny<CancellationToken>()))
            .Returns(async (CommentEntity row, CancellationToken _) =>
            {
                entered.TrySetResult();
                await release.Task;
                return TableMocks.AddRow(h.CommentRows, row);
            });

        var startupPass = h.NoteConversion.RunAsync(default);
        await entered.Task;
        var put = h.ActivityApi.Update(TestRequests.Json(new { gameId = game.Id, name = "Renamed" }), activityId, default);
        Assert.IsFalse(put.IsCompleted, "The PUT must wait for the running pass.");
        Assert.AreEqual("Old text", h.Activities[activityId.ToString("D")].GetString("Comments"));

        release.SetResult();
        await startupPass;
        var result = await put;

        Assert.AreEqual(200, ApiResults.Status(result));
        Assert.AreEqual("Renamed", h.Activities[activityId.ToString("D")].GetString("Name"));
        Assert.AreEqual("Old text", Assert.ContainsSingle(CommentsOf(activityId)).Text);
        Assert.AreEqual(1, CommentAdds(), "The PUT must not run the pass a second time.");
    }

    [TestMethod]
    public async Task AnOldTextOverTheCommentLimitIsCutTo1000CharactersAndAWarningNamesTheActivity()
    {
        var oldText = new string('a', 999) + "bcdef";
        var activityId = SeedActivity("  " + oldText + "  ");
        var exact = SeedActivity(new string('x', 1000));

        await h.NoteConversion.RunAsync(default);

        Assert.AreEqual(new string('a', 999) + "b", Assert.ContainsSingle(CommentsOf(activityId)).Text);
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

    [TestMethod]
    [DataRow(999, " ", "after the cut")]
    [DataRow(995, "          ", "after the cut")]
    public async Task WhitespaceLeftAtTheEndOfTheCutIsTrimmedFromTheComment(int letters, string gap, string tail)
    {
        // The cut at 1000 characters lands on (or inside) the whitespace between the letters and the tail.
        var activityId = SeedActivity(new string('a', letters) + gap + tail);

        await h.NoteConversion.RunAsync(default);

        Assert.AreEqual(new string('a', letters), Assert.ContainsSingle(CommentsOf(activityId)).Text);
    }

    // ---- runs again on every host start: idempotent, and done once

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
        Assert.IsTrue(h.MarkerStored(), "A finished pass stores the marker row.");
        // The marker is the one row of the table that is not a Comment.
        Assert.ContainsSingle(h.CommentRows.Values.Where(r => r is not CommentEntity));
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
        FailCommentInsert(2);

        await Assert.ThrowsAsync<RequestFailedException>(() => h.NoteConversion.RunAsync(default));
        Assert.IsFalse(h.MarkerStored(), "A pass that failed part-way must not store the marker.");
        Assert.HasCount(1, h.StoredComments());

        await h.NoteConversion.RunAsync(default);

        Assert.IsTrue(h.MarkerStored());
        foreach (var activityId in new[] { first, second, third })
            Assert.ContainsSingle(CommentsOf(activityId));
        Assert.HasCount(3, h.StoredComments());
    }

    [TestMethod]
    public async Task DeletingAnActivityAfterTheConversionKeepsTheMarker()
    {
        var activityId = SeedActivity("Doomed");
        await h.NoteConversion.RunAsync(default);
        var marker = h.Marker();
        Assert.HasCount(2, h.CommentRows, "The converted Comment and the marker share the one table.");

        Assert.IsExactInstanceOfType<Microsoft.AspNetCore.Mvc.NoContentResult>(
            await h.ActivityApi.Delete(TestRequests.Empty(), activityId, default));

        Assert.IsEmpty(h.StoredComments());
        Assert.AreSame(marker, h.Marker(), "The Activity-delete cascade deletes Comments only, never the marker.");
        Assert.ContainsSingle(h.CommentRows);
    }

    // ---- read back through the Comment list route

    [TestMethod]
    public async Task TheConvertedCommentIsListedWithTheRowsDateAndNoEvent()
    {
        var lastModified = new DateTimeOffset(2026, 7, 4, 22, 0, 5, TimeSpan.Zero);
        var activityId = SeedRow(row => row["Comments"] = "Bring snacks next time", lastModified, id: OldActivityRows.FixedActivityId);
        await h.NoteConversion.RunAsync(default);

        var listed = ApiResults.Value<List<CommentEntity>>(await h.CommentApi.List(TestRequests.Empty(), activityId, default));

        var comment = Assert.ContainsSingle(listed);
        Assert.AreEqual(OldActivityRows.FixedActivityCommentId, comment.Id);
        Assert.AreEqual(activityId, comment.ActivityId);
        Assert.AreEqual("Bring snacks next time", comment.Text);
        Assert.AreEqual(lastModified, comment.CreatedUtc);
        Assert.IsNull(comment.EventId);
        Assert.AreNotEqual(default(ETag), comment.ETag);
    }

    [TestMethod]
    public async Task TheMarkerRowNeverAppearsInTheListRoute()
    {
        var activityId = SeedActivity("Old text");
        await h.NoteConversion.RunAsync(default);
        Assert.IsTrue(h.MarkerStored());
        // A row of the marker's partition that even carries the Activity's id: only the partition
        // condition of the list query keeps it out.
        var lookalike = Guid.NewGuid().ToString("D");
        h.CommentRows[TableMocks.Key(ActivityNoteConversion.MarkerPartitionKey, lookalike)] =
            new TableEntity(ActivityNoteConversion.MarkerPartitionKey, lookalike) { ["ActivityId"] = activityId, ["Text"] = "Not a Comment" };

        var listed = ApiResults.Value<List<CommentEntity>>(await h.CommentApi.List(TestRequests.Empty(), activityId, default));

        var only = Assert.ContainsSingle(listed);
        Assert.AreEqual("Old text", only.Text);
        Assert.AreEqual(TableNames.CommentPartition, only.PartitionKey);
        Assert.IsTrue(h.MarkerStored());
    }

    // ---- started with the host

    // The startup service resolves the conversion from the host's services when it runs.
    private ServiceProvider HostServices() =>
        new ServiceCollection().AddSingleton(h.NoteConversion).BuildServiceProvider();

    [TestMethod]
    public async Task TheStartupServiceRunsTheConversion()
    {
        var activityId = SeedActivity("Converted at start");
        await using var services = HostServices();
        var startup = new ActivityNoteConversionStartup(services, new ListLogger<ActivityNoteConversionStartup>());

        await startup.StartAsync(default);
        await startup.ExecuteTask!;

        Assert.ContainsSingle(CommentsOf(activityId));
        Assert.IsTrue(h.MarkerStored());
    }

    [TestMethod]
    public async Task AFailedConversionIsLoggedAndNeverStopsTheHost()
    {
        SeedActivity("Will fail");
        var outage = new RequestFailedException(503, "Storage unavailable");
        h.CommentTable.Setup(t => t.AddEntityAsync(It.IsAny<CommentEntity>(), It.IsAny<CancellationToken>())).ThrowsAsync(outage);
        var log = new ListLogger<ActivityNoteConversionStartup>();
        await using var services = HostServices();
        var startup = new ActivityNoteConversionStartup(services, log);

        await startup.StartAsync(default);
        // Awaiting the hosted service's task throws when the failure faulted it.
        await startup.ExecuteTask!;

        var entry = Assert.ContainsSingle(log.Entries);
        Assert.AreEqual(LogLevel.Error, entry.Level);
        Assert.AreSame(outage, entry.Exception);
        Assert.IsFalse(h.MarkerStored());
        await startup.StopAsync(default);
    }

    [TestMethod]
    public async Task AStoreThatFailsWhileTheHostBuildsItIsLoggedAndNeverStopsTheHost()
    {
        // Registered as Program.cs registers them; creating the Comment table fails (storage or auth down).
        var outage = new RequestFailedException(403, "This request is not authorized to perform this operation.");
        var commentTable = new Mock<TableClient>();
        commentTable.Setup(t => t.CreateIfNotExists(It.IsAny<CancellationToken>())).Throws(outage);
        var service = new Mock<TableServiceClient>();
        service.Setup(s => s.GetTableClient(It.IsAny<string>())).Returns(new Mock<TableClient>().Object);
        service.Setup(s => s.GetTableClient(TableNames.Comments)).Returns(commentTable.Object);
        var log = new ListLogger<ActivityNoteConversionStartup>();
        var services = new ServiceCollection();
        services.AddSingleton(service.Object);
        services.AddSingleton(sp => new TableStore<ActivityEntity>(sp.GetRequiredService<TableServiceClient>(), TableNames.Activities, TableNames.ActivityPartition));
        services.AddSingleton(sp => new TableStore<CommentEntity>(sp.GetRequiredService<TableServiceClient>(), TableNames.Comments, TableNames.CommentPartition));
        services.AddSingleton<ILogger<ActivityNoteConversion>>(new ListLogger<ActivityNoteConversion>());
        services.AddSingleton<ILogger<ActivityNoteConversionStartup>>(log);
        services.AddSingleton<ActivityNoteConversion>();
        services.AddHostedService<ActivityNoteConversionStartup>();
        await using var provider = services.BuildServiceProvider();

        // The host resolves every hosted service before it starts them: that must not throw.
        var startup = provider.GetServices<IHostedService>().OfType<ActivityNoteConversionStartup>().Single();
        await startup.StartAsync(default);
        // Awaiting the hosted service's task throws when the failure faulted it.
        await startup.ExecuteTask!;

        var entry = Assert.ContainsSingle(log.Entries);
        Assert.AreEqual(LogLevel.Error, entry.Level);
        Assert.AreSame(outage, entry.Exception);
        await startup.StopAsync(default);
    }

    [TestMethod]
    public async Task TheConversionNeverEditsTheActivityRow()
    {
        var activityId = SeedActivity("Old text");
        var before = h.Activities[activityId.ToString("D")];

        await h.NoteConversion.RunAsync(default);

        Assert.AreSame(before, h.Activities[activityId.ToString("D")]);
        Assert.AreEqual("Old text", before.GetString("Comments"));
        Assert.IsFalse(h.ActivityTable.Invocations.Any(i => i.Method.Name is nameof(TableClient.UpsertEntityAsync)
            or nameof(TableClient.UpdateEntityAsync) or nameof(TableClient.AddEntityAsync) or nameof(TableClient.DeleteEntityAsync)));
    }
}
