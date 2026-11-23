using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// The old-text conversion (#130): each Activity's non-empty old single text becomes that
/// Activity's first Comment, once, through the conversion class over the in-memory tables of
/// <see cref="LibraryHarness"/>.
/// </summary>
[TestClass]
public class ActivityNoteConversionTests : ActivityNoteConversionTestBase
{
    [TestMethod]
    public async Task AnActivityWithAnOldTextGetsOneCommentWithTheTrimmedTextDatedByTheRowsLastWriteAndNoEvent()
    {
        var lastModified = new DateTimeOffset(2026, 8, 2, 19, 45, 12, TimeSpan.Zero);
        var activityId = SeedRow(row => row[OldActivityRows.OldTextColumn] = "  Ran long, cut the second round  ", lastModified,
            id: OldActivityRows.FixedActivityId);

        await h.OldTextConversion.RunAsync(default);

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

        await h.OldTextConversion.RunAsync(default);

        Assert.IsEmpty(CommentsOf(without));
        Assert.ContainsSingle(CommentsOf(withText));
    }

    [TestMethod]
    public async Task ARowWithoutTheOldTextColumnGetsNoComment()
    {
        var withText = SeedActivity("Keep");
        var without = SeedRow(_ => { });
        Assert.IsFalse(h.Activities[without.ToString("D")].ContainsKey(OldActivityRows.OldTextColumn));

        await h.OldTextConversion.RunAsync(default);

        Assert.IsEmpty(CommentsOf(without));
        Assert.ContainsSingle(CommentsOf(withText));
        Assert.IsTrue(h.MarkerStored());
    }

    [TestMethod]
    public async Task ARowWhoseOldTextColumnIsNotAStringIsSkippedWithoutThrowing()
    {
        var withText = SeedActivity("Keep");
        // Table Storage keeps a column's type per row; this one was stored as a number.
        var odd = SeedRow(row => row[OldActivityRows.OldTextColumn] = 42);

        await h.OldTextConversion.RunAsync(default);

        Assert.IsEmpty(CommentsOf(odd));
        Assert.ContainsSingle(CommentsOf(withText));
        Assert.IsTrue(h.MarkerStored(), "The pass finishes and stores the marker.");
    }

    [TestMethod]
    public async Task AnOldTextOverTheCommentLimitIsCutTo1000CharactersAndAWarningNamesTheActivity()
    {
        var oldText = new string('a', 999) + "bcdef";
        var activityId = SeedActivity("  " + oldText + "  ");
        var exact = SeedActivity(new string('x', 1000));

        await h.OldTextConversion.RunAsync(default);

        Assert.AreEqual(new string('a', 999) + "b", Assert.ContainsSingle(CommentsOf(activityId)).Text);
        Assert.AreEqual(new string('x', 1000), Assert.ContainsSingle(CommentsOf(exact)).Text);
        var warning = Assert.ContainsSingle(h.OldTextConversionLog.Entries.Where(e => e.Level == LogLevel.Warning));
        Assert.Contains(activityId.ToString("D"), warning.Message);
        Assert.DoesNotContain(exact.ToString("D"), warning.Message);
    }

    [TestMethod]
    public async Task TheCutNeverSplitsACharacterMadeOfTwoUtf16Units()
    {
        // 999 letters, then an emoji (two UTF-16 units) across the 1000th and 1001st positions.
        var activityId = SeedActivity(new string('a', 999) + "\U0001F3B2" + "tail");

        await h.OldTextConversion.RunAsync(default);

        Assert.AreEqual(new string('a', 999), Assert.ContainsSingle(CommentsOf(activityId)).Text);
    }

    [TestMethod]
    [DataRow(999, " ", "after the cut")]
    [DataRow(995, "          ", "after the cut")]
    public async Task WhitespaceLeftAtTheEndOfTheCutIsTrimmedFromTheComment(int letters, string gap, string tail)
    {
        // The cut at 1000 characters lands on (or inside) the whitespace between the letters and the tail.
        var activityId = SeedActivity(new string('a', letters) + gap + tail);

        await h.OldTextConversion.RunAsync(default);

        Assert.AreEqual(new string('a', letters), Assert.ContainsSingle(CommentsOf(activityId)).Text);
    }

    // ---- runs again on every host start: idempotent, and done once

    [TestMethod]
    public async Task TwoRunsCreateOneCommentAndTheSecondRunWritesNothing()
    {
        var activityId = SeedActivity("Only once");

        await h.OldTextConversion.RunAsync(default);
        var writesAfterFirst = h.Writes();
        await h.OldTextConversion.RunAsync(default);

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
        await h.OldTextConversion.RunAsync(default);
        var comment = Assert.ContainsSingle(CommentsOf(activityId));

        Assert.AreEqual(204, ApiResults.Status(await h.CommentApi.Delete(TestRequests.Empty(), activityId, comment.Id, default)));
        await h.OldTextConversion.RunAsync(default);

        Assert.IsEmpty(CommentsOf(activityId));
        Assert.AreEqual("Delete me", h.Activities[activityId.ToString("D")].GetString(OldActivityRows.OldTextColumn), "The old field is not cleared.");
    }

    [TestMethod]
    public async Task APassThatFailsPartWayStoresNoMarkerAndTheRetryCompletesWithoutDuplicates()
    {
        var first = SeedActivity("First");
        var second = SeedActivity("Second");
        var third = SeedActivity("Third");
        // The second Comment insert fails with a storage outage; every other insert behaves like storage.
        FailCommentInsert(2);

        await Assert.ThrowsAsync<RequestFailedException>(() => h.OldTextConversion.RunAsync(default));
        Assert.IsFalse(h.MarkerStored(), "A pass that failed part-way must not store the marker.");
        Assert.HasCount(1, h.StoredComments());

        await h.OldTextConversion.RunAsync(default);

        Assert.IsTrue(h.MarkerStored());
        foreach (var activityId in new[] { first, second, third })
            Assert.ContainsSingle(CommentsOf(activityId));
        Assert.HasCount(3, h.StoredComments());
    }

    [TestMethod]
    public async Task DeletingAnActivityAfterTheConversionKeepsTheMarker()
    {
        var activityId = SeedActivity("Doomed");
        await h.OldTextConversion.RunAsync(default);
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
        var activityId = SeedRow(row => row[OldActivityRows.OldTextColumn] = "Bring snacks next time", lastModified, id: OldActivityRows.FixedActivityId);
        await h.OldTextConversion.RunAsync(default);

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
        await h.OldTextConversion.RunAsync(default);
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

    [TestMethod]
    public async Task TheConversionNeverEditsTheActivityRow()
    {
        var activityId = SeedActivity("Old text");
        var before = h.Activities[activityId.ToString("D")];

        await h.OldTextConversion.RunAsync(default);

        Assert.AreSame(before, h.Activities[activityId.ToString("D")]);
        Assert.AreEqual("Old text", before.GetString(OldActivityRows.OldTextColumn));
        Assert.IsFalse(h.ActivityTable.Invocations.Any(i => i.Method.Name is nameof(TableClient.UpsertEntityAsync)
            or nameof(TableClient.UpdateEntityAsync) or nameof(TableClient.AddEntityAsync) or nameof(TableClient.DeleteEntityAsync)));
    }
}
