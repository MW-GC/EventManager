using Azure.Data.Tables;
using Moq;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>The old-text conversion and the Activity PUT (#133): a PUT never replaces a row whose old text is not converted yet.</summary>
[TestClass]
public class ActivityNoteConversionPutTests : ActivityNoteConversionTestBase
{
    [TestMethod]
    public async Task AnActivityPutAfterTheConversionNeitherBringsBackNorNeedsTheOldColumn()
    {
        var game = h.SeedGame();
        var activityId = SeedRow(row => row[OldActivityRows.OldTextColumn] = "Old text", gameId: game.Id);
        await h.OldTextConversion.RunAsync(default);
        var convertedId = Assert.ContainsSingle(CommentsOf(activityId)).Id;

        // A client that still sends the old field, and one that never heard of it.
        var withOld = await h.ActivityApi.Update(
            TestRequests.Json(new { gameId = game.Id, name = "Renamed", comments = "Sneaked back in" }), activityId, default);
        Assert.AreEqual(200, ApiResults.Status(withOld));
        Assert.IsFalse(h.Activities[activityId.ToString("D")].ContainsKey(OldActivityRows.OldTextColumn), "The PUT must not store the old column.");
        var without = await h.ActivityApi.Update(TestRequests.Json(new { gameId = game.Id, name = "Renamed again" }), activityId, default);
        Assert.AreEqual(200, ApiResults.Status(without));
        Assert.IsFalse(h.Activities[activityId.ToString("D")].ContainsKey(OldActivityRows.OldTextColumn));
        Assert.AreEqual("Renamed again", h.Activities[activityId.ToString("D")].GetString("Name"));

        // The Comment the conversion made is kept, read again after the PUTs, and a later start adds nothing.
        await h.OldTextConversion.RunAsync(default);
        var listed = ApiResults.Value<List<CommentEntity>>(await h.CommentApi.List(TestRequests.Empty(), activityId, default));
        var kept = Assert.ContainsSingle(listed);
        Assert.AreEqual(convertedId, kept.Id);
        Assert.AreEqual("Old text", kept.Text);
    }

    [TestMethod]
    public async Task APutBeforeAnyPassKeepsTheOldTextAsACommentAndASecondPutAddsNone()
    {
        var game = h.SeedGame();
        var lastModified = new DateTimeOffset(2026, 6, 1, 18, 0, 0, TimeSpan.Zero);
        var activityId = SeedRow(row => row[OldActivityRows.OldTextColumn] = "  Old text  ", lastModified, game.Id);
        Assert.IsFalse(h.MarkerStored(), "No pass has run: the startup service never started.");

        var first = await h.ActivityApi.Update(TestRequests.Json(new { gameId = game.Id, name = "Renamed" }), activityId, default);

        Assert.AreEqual(200, ApiResults.Status(first));
        var row = h.Activities[activityId.ToString("D")];
        Assert.AreEqual("Renamed", row.GetString("Name"));
        Assert.IsFalse(row.ContainsKey(OldActivityRows.OldTextColumn), "The PUT did replace the row.");
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
        var activityId = SeedRow(row => row[OldActivityRows.OldTextColumn] = "Old text", gameId: game.Id);
        var before = h.Activities[activityId.ToString("D")];
        // The first Comment insert fails with a storage outage; later inserts behave like storage.
        FailCommentInsert(1);

        var failed = await h.ActivityApi.Update(TestRequests.Json(new { gameId = game.Id, name = "Renamed" }), activityId, default);

        Assert.AreEqual(503, ApiResults.Status(failed));
        Assert.AreEqual("Storage is temporarily unavailable. Try again shortly.", ApiResults.Message(failed));
        Assert.AreSame(before, h.Activities[activityId.ToString("D")], "The row must not be replaced.");
        Assert.AreEqual("Old text", before.GetString(OldActivityRows.OldTextColumn));
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
        Assert.IsFalse(row.ContainsKey(OldActivityRows.OldTextColumn));
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
        var activityId = SeedRow(row => row[OldActivityRows.OldTextColumn] = "Old text", gameId: game.Id);
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

        var startupPass = h.OldTextConversion.RunAsync(default);
        await entered.Task;
        var put = h.ActivityApi.Update(TestRequests.Json(new { gameId = game.Id, name = "Renamed" }), activityId, default);
        Assert.IsFalse(put.IsCompleted, "The PUT must wait for the running pass.");
        Assert.AreEqual("Old text", h.Activities[activityId.ToString("D")].GetString(OldActivityRows.OldTextColumn));

        release.SetResult();
        await startupPass;
        var result = await put;

        Assert.AreEqual(200, ApiResults.Status(result));
        Assert.AreEqual("Renamed", h.Activities[activityId.ToString("D")].GetString("Name"));
        Assert.AreEqual("Old text", Assert.ContainsSingle(CommentsOf(activityId)).Text);
        Assert.AreEqual(1, CommentAdds(), "The PUT must not run the pass a second time.");
    }
}
