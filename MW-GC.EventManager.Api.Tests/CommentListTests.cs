using Azure;
using Microsoft.AspNetCore.Mvc;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>Listing an Activity's Comments (#129, #160): only that Activity's, in order, queried by Activity.</summary>
[TestClass]
public class CommentListTests : CommentApiTestBase
{
    // ---- list

    [TestMethod]
    public async Task ListReturnsOnlyThatActivitysCommentsOrderedByAddedDateThenId()
    {
        var (activityId, _) = await CreateActivity("A");
        var (otherId, _) = await CreateActivity("B");
        var t = new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);
        var late = Seed(activityId, "late", t.AddHours(2));
        var tieSecond = Seed(activityId, "tie 2", t, Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var tieFirst = Seed(activityId, "tie 1", t, Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var early = Seed(activityId, "early", t.AddHours(-1));
        var other = Seed(otherId, "someone else's", t.AddHours(-5));

        var listed = await List(activityId);

        Assert.AreSequenceEqual(new[] { early.Id, tieFirst.Id, tieSecond.Id, late.Id }, listed.Select(c => c.Id));
        Assert.AreEqual(other.Id, Assert.ContainsSingle(await List(otherId)).Id);
        foreach (var comment in listed)
            Assert.AreNotEqual(default(ETag), comment.ETag, "Every listed Comment carries its own eTag.");
    }

    [TestMethod]
    public async Task ListOfAnActivityWithNoCommentsIsEmptyAndOfAnUnknownActivityIs404()
    {
        var (activityId, _) = await CreateActivity();

        Assert.IsEmpty(await List(activityId));
        Assert.IsExactInstanceOfType<NotFoundResult>(await h.CommentApi.List(Bare(), Guid.NewGuid(), default));
    }

    [TestMethod]
    public async Task ListAndActivityDeleteQueryTheTableByActivity()
    {
        var (a, aTag) = await CreateActivity("A");
        var (b, _) = await CreateActivity("B");
        var (mine, _) = await AddComment(a, "Mine");
        var (theirs, _) = await AddComment(b, "Theirs");
        Seed(b, "Theirs too", DateTimeOffset.UtcNow);

        Assert.AreEqual(mine.Id, Assert.ContainsSingle(await List(a)).Id);
        Assert.IsExactInstanceOfType<NoContentResult>(await h.ActivityApi.Delete(Bare(aTag), a, default));

        // The list and the Activity delete each sent the ActivityId filter to the table, so the table
        // answered each query with A's one Comment and never with B's two. (The in-memory table
        // evaluates the query it is sent; Azurite checks the same in StorageQueryAzuriteTests.)
        CollectionAssert.AreEqual(new[] { 1, 1 }, h.CommentRowsRead);
        Assert.IsNull(h.StoredComment(mine.Id));
        Assert.IsNotNull(h.StoredComment(theirs.Id));
        Assert.HasCount(2, await List(b));
    }
}
