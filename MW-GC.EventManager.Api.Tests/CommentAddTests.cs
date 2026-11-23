using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>Adding a Comment (#129) and the text rule the add and edit routes share.</summary>
[TestClass]
public class CommentAddTests : CommentApiTestBase
{
    // ---- add

    [TestMethod]
    public async Task AddStoresTheCommentInItsOwnTableAndAnswers201WithLocationBodyAndETag()
    {
        var (activityId, _) = await CreateActivity();
        var eventId = SeedEvent();
        var request = Request(new { text = "  Ran long, cut the second round  ", eventId });

        var result = await h.CommentApi.Add(request, activityId, default);

        var created = Assert.IsExactInstanceOfType<CreatedResult>(result);
        var comment = Assert.IsExactInstanceOfType<CommentEntity>(created.Value);
        Assert.AreEqual($"/api/activities/{activityId}/comments/{comment.Id}", created.Location);
        Assert.AreNotEqual(Guid.Empty, comment.Id);
        Assert.AreEqual(activityId, comment.ActivityId);
        Assert.AreEqual("Ran long, cut the second round", comment.Text);
        Assert.AreEqual(eventId, comment.EventId);
        var etag = ETagHeader(request);
        Assert.IsNotNull(etag, "POST must answer with the new row's ETag header.");
        Assert.AreEqual(etag, comment.ETag.ToString());

        var stored = h.StoredComment(comment.Id);
        Assert.IsNotNull(stored, "The Comment is a row of the ActivityComments table.");
        Assert.AreEqual(TableNames.CommentPartition, stored.PartitionKey);
        Assert.AreEqual("Ran long, cut the second round", stored.Text);
        // The Activity row is not touched: no old-text column appears on it.
        Assert.IsNull(h.Activities[activityId.ToString("D")].GetString(OldActivityRows.OldTextColumn));
        Assert.AreEqual(1, Calls(h.ActivityTable, nameof(TableClient.UpsertEntityAsync)));
        Assert.AreEqual(0, Calls(h.ActivityTable, nameof(TableClient.UpdateEntityAsync)));
    }

    [TestMethod]
    public async Task AddSetsTheAddedDateAndIdsItselfAndIgnoresClientSuppliedValues()
    {
        var (activityId, _) = await CreateActivity();
        var sentId = Guid.NewGuid();
        var before = DateTimeOffset.UtcNow;

        var result = await h.CommentApi.Add(
            Request(new { text = "Hi", createdUtc = "2000-01-01T00:00:00Z", id = sentId, activityId = Guid.NewGuid() }), activityId, default);

        var after = DateTimeOffset.UtcNow;
        var comment = ApiResults.Value<CommentEntity>(result);
        Assert.IsTrue(comment.CreatedUtc >= before && comment.CreatedUtc <= after, $"CreatedUtc {comment.CreatedUtc:O} is not the time of the request.");
        Assert.AreEqual(TimeSpan.Zero, comment.CreatedUtc.Offset);
        Assert.AreNotEqual(sentId, comment.Id);
        Assert.AreEqual(activityId, comment.ActivityId);
        Assert.IsNull(comment.EventId);
        Assert.AreEqual(comment.CreatedUtc, h.StoredComment(comment.Id)!.CreatedUtc);
    }

    [TestMethod]
    public async Task AddToAnUnknownActivityIs404AndWritesNothing()
    {
        var result = await h.CommentApi.Add(Request(new { text = "Orphan" }), Guid.NewGuid(), default);

        Assert.IsExactInstanceOfType<NotFoundResult>(result);
        Assert.IsEmpty(h.StoredComments());
        Assert.AreEqual(0, h.Writes());
    }

    [TestMethod]
    public async Task AddWithAnUnknownEventIs400AndWritesNothing()
    {
        var (activityId, _) = await CreateActivity();

        var result = await h.CommentApi.Add(Request(new { text = "From nowhere", eventId = Guid.NewGuid() }), activityId, default);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(result));
        Assert.AreEqual(EventNotFound, ApiResults.Message(result));
        Assert.IsEmpty(h.StoredComments());
        Assert.AreEqual(0, CommentWrites());
    }

    // ---- text rule, shared by add and edit

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"text\":\"\"}")]
    [DataRow("{\"text\":\"   \"}")]
    [DataRow("{\"text\":null}")]
    public async Task BlankTextIs400OnAddAndEditAndNothingChanges(string body)
    {
        var (activityId, _) = await CreateActivity();
        var (comment, _) = await AddComment(activityId, "Keep me");

        var add = await h.CommentApi.Add(TestRequests.Raw(body), activityId, default);
        var edit = await h.CommentApi.Update(TestRequests.Raw(body), activityId, comment.Id, default);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(add));
        Assert.AreEqual(TextRequired, ApiResults.Message(add));
        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(edit));
        Assert.AreEqual(TextRequired, ApiResults.Message(edit));
        Assert.ContainsSingle(h.StoredComments());
        Assert.AreEqual("Keep me", h.StoredComment(comment.Id)!.Text);
        Assert.AreEqual(1, CommentWrites());
    }

    [TestMethod]
    public async Task TextOf1000CharactersIsAcceptedAnd1001IsRefusedOnAddAndEdit()
    {
        var (activityId, _) = await CreateActivity();
        var limit = new string('c', 1000);
        var over = new string('c', 1001);

        var (comment, _) = await AddComment(activityId, limit);
        Assert.AreEqual(limit, comment.Text);
        // The limit applies to the trimmed text.
        var (padded, _) = await AddComment(activityId, "  " + limit + "  ");
        Assert.AreEqual(limit, padded.Text);

        var add = await h.CommentApi.Add(Request(new { text = over }), activityId, default);
        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(add));
        Assert.AreEqual(TextTooLong, ApiResults.Message(add));
        Assert.HasCount(2, h.StoredComments());

        var edit = await h.CommentApi.Update(Request(new { text = over }), activityId, comment.Id, default);
        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(edit));
        Assert.AreEqual(TextTooLong, ApiResults.Message(edit));
        Assert.AreEqual(limit, h.StoredComment(comment.Id)!.Text);

        var editOk = await h.CommentApi.Update(Request(new { text = new string('d', 1000) }), activityId, comment.Id, default);
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(editOk));
        Assert.AreEqual(new string('d', 1000), h.StoredComment(comment.Id)!.Text);
    }

    [TestMethod]
    [DataRow("{\"text\":", "application/json", StatusCodes.Status400BadRequest, RequestBody.InvalidJsonMessage)]
    [DataRow("{\"text\":\"Valid\"}", "text/plain", StatusCodes.Status415UnsupportedMediaType, RequestBody.UnsupportedMediaTypeMessage)]
    [DataRow("null", "application/json", StatusCodes.Status400BadRequest, RequestBody.NullBodyMessage)]
    public async Task BadBodiesAreRefusedOnAddAndEditLikeEveryOtherRoute(string body, string contentType, int status, string message)
    {
        var (activityId, _) = await CreateActivity();
        var (comment, _) = await AddComment(activityId, "Keep me");

        var add = await h.CommentApi.Add(TestRequests.Raw(body, contentType), activityId, default);
        var edit = await h.CommentApi.Update(TestRequests.Raw(body, contentType), activityId, comment.Id, default);

        Assert.AreEqual(status, ApiResults.Status(add));
        Assert.AreEqual(message, ApiResults.Message(add));
        Assert.AreEqual(status, ApiResults.Status(edit));
        Assert.AreEqual(message, ApiResults.Message(edit));
        Assert.AreEqual("Keep me", Assert.ContainsSingle(h.StoredComments()).Text);
        Assert.AreEqual(1, CommentWrites());
    }
}
