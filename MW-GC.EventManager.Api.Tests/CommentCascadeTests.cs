using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>Comments and the Activity and Event routes (ADR 0001): an Activity delete deletes them; an Event delete and an Activity duplicate do not.</summary>
[TestClass]
public class CommentCascadeTests : CommentApiTestBase
{
    // ---- Activity delete cascades; Event delete and Activity duplicate do not touch Comments

    [TestMethod]
    public async Task DeletingAnActivityDeletesItsCommentsAndNoOthers()
    {
        var (doomed, doomedTag) = await CreateActivity("Doomed");
        var (other, _) = await CreateActivity("Other");
        await AddComment(doomed, "One");
        await AddComment(doomed, "Two");
        var (kept, _) = await AddComment(other, "Not mine to lose");

        Assert.IsExactInstanceOfType<NoContentResult>(await h.ActivityApi.Delete(Bare(doomedTag), doomed, default));

        Assert.IsFalse(h.Activities.ContainsKey(doomed.ToString("D")));
        Assert.AreEqual(kept.Id, Assert.ContainsSingle(h.StoredComments()).Id);
        Assert.IsExactInstanceOfType<NotFoundResult>(await h.CommentApi.List(Bare(), doomed, default));
    }

    [TestMethod]
    public async Task RetryingAnActivityDeleteAfterItsRowIsGoneStillDeletesItsComments()
    {
        // A first delete removed the Activity row but failed before its Comments were gone.
        var gone = Guid.NewGuid();
        Seed(gone, "Left behind", DateTimeOffset.UtcNow);
        Seed(gone, "Also left behind", DateTimeOffset.UtcNow);
        var (other, _) = await CreateActivity();
        var (kept, _) = await AddComment(other, "Stays");

        Assert.IsExactInstanceOfType<NoContentResult>(await h.ActivityApi.Delete(Bare(), gone, default));

        Assert.AreEqual(kept.Id, Assert.ContainsSingle(h.StoredComments()).Id);
    }

    [TestMethod]
    public async Task AStaleActivityDeleteIs409AndDeletesNoComment()
    {
        var (activityId, original) = await CreateActivity();
        var (comment, _) = await AddComment(activityId, "Stay");
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(
            await h.ActivityApi.Update(Request(new { name = "Newer", gameId = game.Id }, original), activityId, default)));

        var result = await h.ActivityApi.Delete(Bare(original), activityId, default);

        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(result));
        Assert.IsTrue(h.Activities.ContainsKey(activityId.ToString("D")));
        Assert.IsNotNull(h.StoredComment(comment.Id));
        Assert.AreEqual(0, Calls(h.CommentTable, nameof(TableClient.DeleteEntityAsync)));
    }

    [TestMethod]
    public async Task ACommentCleanupFailureIsLoggedAnd503()
    {
        var (activityId, _) = await CreateActivity();
        var (comment, _) = await AddComment(activityId, "Stuck");
        var outage = new RequestFailedException(503, "Storage unavailable");
        h.CommentTable.Setup(t => t.DeleteEntityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ETag>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(outage);

        var result = await h.ActivityApi.Delete(Bare(), activityId, default);

        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, ApiResults.Status(result));
        Assert.AreEqual(RouteFailures.UnavailableMessage, ApiResults.Message(result));
        var entry = Assert.ContainsSingle(h.ActivityLog.Entries);
        Assert.AreSame(outage, entry.Exception);
        Assert.Contains("DeleteActivity", entry.Message);
        // The Comment is still there, so a retried delete (204 on a missing Activity) cleans it up.
        Assert.IsNotNull(h.StoredComment(comment.Id));
    }

    [TestMethod]
    public async Task DeletingAnEventLeavesItsCommentsInPlaceWithTheEventId()
    {
        var (activityId, _) = await CreateActivity();
        var eventId = SeedEvent();
        var (comment, _) = await AddComment(activityId, "From Friday", eventId);

        Assert.IsExactInstanceOfType<NoContentResult>(await h.EventApi.Delete(Bare(), eventId, default));

        Assert.IsFalse(h.Events.ContainsKey(eventId.ToString("D")));
        var listed = Assert.ContainsSingle(await List(activityId));
        Assert.AreEqual(comment.Id, listed.Id);
        Assert.AreEqual(eventId, listed.EventId);
    }

    [TestMethod]
    public async Task DuplicatingAnActivityCopiesNoComments()
    {
        var (activityId, _) = await CreateActivity();
        await AddComment(activityId, "Original only");

        var copy = ApiResults.Value<ActivityEntity>(await h.ActivityApi.Duplicate(Bare(), activityId, default));

        Assert.IsEmpty(await List(copy.Id));
        Assert.ContainsSingle(await List(activityId));
        Assert.ContainsSingle(h.StoredComments());
    }
}
