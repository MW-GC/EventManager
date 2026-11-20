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
/// The old-note conversion (#130): each Activity's non-empty old single note becomes that
/// Activity's first Comment, once, through the conversion class over the in-memory tables of
/// <see cref="LibraryHarness"/>.
/// </summary>
[TestClass]
public class ActivityNoteConversionTests
{
    private readonly LibraryHarness h = new();

    // An Activity row as an older version stored it, with the old single text in its raw Comments
    // column and the time storage last wrote it. The Activity itself no longer has that field (#133).
    private Guid SeedActivity(string? note, DateTimeOffset? lastModified = null) =>
        SeedRow(row => row["Comments"] = note, lastModified);

    private Guid SeedRow(Action<TableEntity> oldColumn, DateTimeOffset? lastModified = null, Guid? gameId = null)
    {
        var id = Guid.NewGuid();
        var row = new TableEntity("Activity", id.ToString("D"))
        {
            ["GameId"] = gameId ?? Guid.NewGuid(),
            ["Name"] = "Old activity",
            ["ThemeIds"] = "[]",
            ["HolidayIds"] = "[]",
            Timestamp = lastModified ?? new DateTimeOffset(2026, 9, 14, 20, 30, 0, TimeSpan.Zero),
        };
        oldColumn(row);
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
    public async Task ARowWithoutTheOldCommentsColumnGetsNoComment()
    {
        var withText = SeedActivity("Keep");
        var without = SeedRow(_ => { });
        Assert.IsFalse(h.Activities[without.ToString("D")].ContainsKey("Comments"));

        await h.NoteConversion.RunAsync(default);

        Assert.IsEmpty(CommentsOf(without));
        Assert.ContainsSingle(CommentsOf(withText));
        Assert.IsTrue(MarkerStored());
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
        Assert.IsTrue(MarkerStored(), "The pass finishes and stores the marker.");
    }

    [TestMethod]
    public async Task AnActivityPutAfterTheConversionNeitherBringsBackNorNeedsTheOldColumn()
    {
        var game = h.SeedGame();
        var activityId = SeedRow(row => row["Comments"] = "Old text", gameId: game.Id);
        await h.NoteConversion.RunAsync(default);
        var converted = Assert.ContainsSingle(CommentsOf(activityId));

        // A client that still sends the old field, and one that never heard of it.
        var withOld = await h.ActivityApi.Update(
            TestRequests.Json(new { gameId = game.Id, name = "Renamed", comments = "Sneaked back in" }), activityId, default);
        Assert.AreEqual(200, ApiResults.Status(withOld));
        Assert.IsFalse(h.Activities[activityId.ToString("D")].ContainsKey("Comments"), "The PUT must not store the old column.");
        var without = await h.ActivityApi.Update(TestRequests.Json(new { gameId = game.Id, name = "Renamed again" }), activityId, default);
        Assert.AreEqual(200, ApiResults.Status(without));
        Assert.IsFalse(h.Activities[activityId.ToString("D")].ContainsKey("Comments"));
        Assert.AreEqual("Renamed again", h.Activities[activityId.ToString("D")].GetString("Name"));

        // The Comment the conversion made is kept, and a later start adds nothing.
        await h.NoteConversion.RunAsync(default);
        Assert.AreEqual(converted.Id, Assert.ContainsSingle(CommentsOf(activityId)).Id);
        Assert.AreEqual("Old text", converted.Text);
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
        Assert.IsTrue(MarkerStored());
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
        await startup.ExecuteTask!;

        Assert.IsTrue(startup.ExecuteTask!.IsCompletedSuccessfully, "The failure must not fault the hosted service.");
        var entry = Assert.ContainsSingle(log.Entries);
        Assert.AreEqual(LogLevel.Error, entry.Level);
        Assert.AreSame(outage, entry.Exception);
        Assert.IsFalse(MarkerStored());
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
        service.Setup(s => s.GetTableClient("ActivityComments")).Returns(commentTable.Object);
        var log = new ListLogger<ActivityNoteConversionStartup>();
        var services = new ServiceCollection();
        services.AddSingleton(service.Object);
        services.AddSingleton(sp => new TableStore<ActivityEntity>(sp.GetRequiredService<TableServiceClient>(), "Activities", "Activity"));
        services.AddSingleton(sp => new TableStore<CommentEntity>(sp.GetRequiredService<TableServiceClient>(), "ActivityComments", "Comment"));
        services.AddSingleton<ILogger<ActivityNoteConversion>>(new ListLogger<ActivityNoteConversion>());
        services.AddSingleton<ILogger<ActivityNoteConversionStartup>>(log);
        services.AddSingleton<ActivityNoteConversion>();
        services.AddHostedService<ActivityNoteConversionStartup>();
        await using var provider = services.BuildServiceProvider();

        // The host resolves every hosted service before it starts them: that must not throw.
        var startup = provider.GetServices<IHostedService>().OfType<ActivityNoteConversionStartup>().Single();
        await startup.StartAsync(default);
        await startup.ExecuteTask!;

        Assert.IsTrue(startup.ExecuteTask!.IsCompletedSuccessfully, "The failure must not fault the hosted service.");
        var entry = Assert.ContainsSingle(log.Entries);
        Assert.AreEqual(LogLevel.Error, entry.Level);
        Assert.AreSame(outage, entry.Exception);
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
