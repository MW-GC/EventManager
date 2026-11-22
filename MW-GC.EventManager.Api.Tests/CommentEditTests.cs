using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>Editing a Comment (#129): only the text changes, under its own eTag.</summary>
[TestClass]
public class CommentEditTests : CommentApiTestBase
{
    // ---- edit

    [TestMethod]
    public async Task EditChangesOnlyTheTextAndKeepsAddedDateEventAndActivity()
    {
        var (activityId, _) = await CreateActivity();
        var eventId = SeedEvent();
        var (comment, etag) = await AddComment(activityId, "Typo", eventId);
        var createdUtc = comment.CreatedUtc;
        var request = Request(new { text = " Fixed ", createdUtc = "2000-01-01T00:00:00Z", activityId = Guid.NewGuid(), eventId = Guid.NewGuid() }, etag);

        var result = await h.CommentApi.Update(request, activityId, comment.Id, default);

        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(result));
        var edited = ApiResults.Value<CommentEntity>(result);
        var stored = h.StoredComment(comment.Id)!;
        foreach (var actual in new[] { edited, stored })
        {
            Assert.AreEqual(comment.Id, actual.Id);
            Assert.AreEqual("Fixed", actual.Text);
            Assert.AreEqual(createdUtc, actual.CreatedUtc);
            Assert.AreEqual(activityId, actual.ActivityId);
            Assert.AreEqual(eventId, actual.EventId);
        }
        var fresh = ETagHeader(request);
        Assert.IsNotNull(fresh);
        Assert.AreNotEqual(etag, fresh);
        Assert.AreEqual(fresh, edited.ETag.ToString());
    }

    [TestMethod]
    public async Task EditOfACommentOfAnotherActivityOrOfAMissingCommentIs404()
    {
        var (owner, _) = await CreateActivity("Owner");
        var (other, _) = await CreateActivity("Other");
        var (comment, _) = await AddComment(owner, "Mine");

        var wrongActivity = await h.CommentApi.Update(Request(new { text = "Hijacked" }), other, comment.Id, default);
        var missing = await h.CommentApi.Update(Request(new { text = "Ghost" }), owner, Guid.NewGuid(), default);

        Assert.IsExactInstanceOfType<NotFoundResult>(wrongActivity);
        Assert.IsExactInstanceOfType<NotFoundResult>(missing);
        Assert.AreEqual("Mine", h.StoredComment(comment.Id)!.Text);
        Assert.ContainsSingle(h.StoredComments());
        Assert.AreEqual(1, CommentWrites());
    }

    [TestMethod]
    public async Task EditWithAStaleIfMatchIs409AndLeavesTheText()
    {
        var (activityId, _) = await CreateActivity();
        var (comment, original) = await AddComment(activityId, "Original");
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(
            await h.CommentApi.Update(Request(new { text = "First editor" }, original), activityId, comment.Id, default)));

        var result = await h.CommentApi.Update(Request(new { text = "Second editor" }, original), activityId, comment.Id, default);

        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(result));
        Assert.AreEqual(RouteFailures.ConflictMessage, ApiResults.Message(result));
        Assert.AreEqual("First editor", h.StoredComment(comment.Id)!.Text);
    }

    [TestMethod]
    public async Task EditingTwoCommentsOfOneActivityInTurnNeverConflicts()
    {
        var (activityId, activityTag) = await CreateActivity();
        var (first, firstTag) = await AddComment(activityId, "One");
        var (second, secondTag) = await AddComment(activityId, "Two");

        // Each Comment has its own eTag: editing one never stales the other, or the Activity.
        var editFirst = Request(new { text = "One edited" }, firstTag);
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(await h.CommentApi.Update(editFirst, activityId, first.Id, default)));
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(
            await h.CommentApi.Update(Request(new { text = "Two edited" }, secondTag), activityId, second.Id, default)));
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(
            await h.ActivityApi.Update(Request(new { name = "Renamed", gameId = game.Id }, activityTag), activityId, default)));
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(
            await h.CommentApi.Update(Request(new { text = "One again" }, ETagHeader(editFirst)), activityId, first.Id, default)));

        Assert.AreEqual("One again", h.StoredComment(first.Id)!.Text);
        Assert.AreEqual("Two edited", h.StoredComment(second.Id)!.Text);
        Assert.AreEqual("Renamed", h.Activities[activityId.ToString("D")].GetString("Name"));
    }
}
