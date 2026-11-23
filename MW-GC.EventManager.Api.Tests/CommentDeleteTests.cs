using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MW_GC.EventManager.API.Validation;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>Deleting a Comment (#129): idempotent, conditional, and never another Activity's.</summary>
[TestClass]
public class CommentDeleteTests : CommentApiTestBase
{
    // ---- delete

    [TestMethod]
    public async Task DeleteRemovesTheCommentAndARepeatIsAnIdempotent204()
    {
        var (activityId, _) = await CreateActivity();
        var (comment, etag) = await AddComment(activityId, "Wrong remark");
        var (kept, _) = await AddComment(activityId, "Right remark");

        Assert.IsExactInstanceOfType<NoContentResult>(await h.CommentApi.Delete(Bare(etag), activityId, comment.Id, default));
        Assert.IsNull(h.StoredComment(comment.Id));
        Assert.AreEqual(kept.Id, Assert.ContainsSingle(await List(activityId)).Id);

        // Gone: a repeat delete is still 204, with or without a tag, and so is an id never used.
        Assert.IsExactInstanceOfType<NoContentResult>(await h.CommentApi.Delete(Bare(etag), activityId, comment.Id, default));
        Assert.IsExactInstanceOfType<NoContentResult>(await h.CommentApi.Delete(Bare(), activityId, comment.Id, default));
        Assert.IsExactInstanceOfType<NoContentResult>(await h.CommentApi.Delete(Bare("*"), activityId, Guid.NewGuid(), default));
        Assert.IsNotNull(h.StoredComment(kept.Id));
    }

    [TestMethod]
    public async Task DeleteWithAStaleIfMatchIs409AndKeepsTheComment()
    {
        var (activityId, _) = await CreateActivity();
        var (comment, original) = await AddComment(activityId, "Original");
        var edit = Request(new { text = "Newer" }, original);
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(await h.CommentApi.Update(edit, activityId, comment.Id, default)));

        var stale = await h.CommentApi.Delete(Bare(original), activityId, comment.Id, default);

        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(stale));
        Assert.AreEqual(RouteFailures.ConflictMessage, ApiResults.Message(stale));
        Assert.AreEqual("Newer", h.StoredComment(comment.Id)!.Text);
        Assert.IsExactInstanceOfType<NoContentResult>(await h.CommentApi.Delete(Bare(ETagHeader(edit)), activityId, comment.Id, default));
        Assert.IsNull(h.StoredComment(comment.Id));
    }

    [TestMethod]
    public async Task DeleteOfACommentOfAnotherActivityIs204AndDeletesNothing()
    {
        var (owner, _) = await CreateActivity("Owner");
        var (other, _) = await CreateActivity("Other");
        var (comment, _) = await AddComment(owner, "Mine");

        Assert.IsExactInstanceOfType<NoContentResult>(await h.CommentApi.Delete(Bare(), other, comment.Id, default));

        Assert.IsNotNull(h.StoredComment(comment.Id));
        Assert.AreEqual(0, Calls(h.CommentTable, nameof(TableClient.DeleteEntityAsync)));
    }
}
