using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// An Activity's Comments (#129, ADR 0001) through the real Functions classes over in-memory tables:
/// each Comment is its own row in ActivityComments with its own eTag, listed, added, edited and
/// deleted under its Activity, and deleted with that Activity.
/// </summary>
[TestClass]
public class ActivityCommentApiTests
{
    private const string TextRequired = "Comment text is required.";
    private const string TextTooLong = "Comment text must be 1000 characters or fewer.";
    private const string EventNotFound = "Event not found.";

    private readonly LibraryHarness h = new();
    private readonly GameEntity game;

    public ActivityCommentApiTests() => game = h.SeedGame("Owner");

    // ---- helpers

    private static HttpRequest Request(object body, string? ifMatch = null)
    {
        var request = TestRequests.Json(body);
        if (ifMatch is not null) request.Headers["If-Match"] = ifMatch;
        return request;
    }

    private static HttpRequest Bare(string? ifMatch = null)
    {
        var request = TestRequests.Empty();
        if (ifMatch is not null) request.Headers["If-Match"] = ifMatch;
        return request;
    }

    private static string? ETagHeader(HttpRequest request) =>
        request.HttpContext.Response.Headers.ETag.Count == 0 ? null : request.HttpContext.Response.Headers.ETag.ToString();

    private async Task<(Guid Id, string ETag)> CreateActivity(string name = "Act")
    {
        var request = Request(new { name, gameId = game.Id });
        var result = await h.ActivityApi.Create(request, default);
        Assert.AreEqual(StatusCodes.Status201Created, ApiResults.Status(result));
        return (ApiResults.Value<ActivityEntity>(result).Id, ETagHeader(request)!);
    }

    private Guid SeedEvent()
    {
        var id = Guid.NewGuid().ToString("D");
        h.Events[id] = new TableEntity("Event", id) { ["Name"] = "Friday", ["Selections"] = "[]", ["UniqueGamesOnly"] = true };
        return Guid.Parse(id);
    }

    private async Task<(ActivityCommentEntity Comment, string ETag)> AddComment(Guid activityId, string text, Guid? eventId = null)
    {
        var request = Request(new { text, eventId });
        var result = await h.CommentApi.Add(request, activityId, default);
        Assert.AreEqual(StatusCodes.Status201Created, ApiResults.Status(result));
        return (ApiResults.Value<ActivityCommentEntity>(result), ETagHeader(request)!);
    }

    // A Comment written straight into the in-memory table, for a chosen added date or id.
    private ActivityCommentEntity Seed(Guid activityId, string text, DateTimeOffset createdUtc, Guid? id = null)
    {
        var comment = new ActivityCommentEntity
        {
            Id = id ?? Guid.NewGuid(), PartitionKey = "Comment", ActivityId = activityId, Text = text,
            CreatedUtc = createdUtc, ETag = TableMocks.NextETag(),
        };
        h.ActivityComments[comment.RowKey] = comment;
        return comment;
    }

    private async Task<List<ActivityCommentEntity>> List(Guid activityId) =>
        ApiResults.Value<List<ActivityCommentEntity>>(await h.CommentApi.List(Bare(), activityId, default));

    private ActivityCommentEntity? Stored(Guid commentId) =>
        h.ActivityComments.TryGetValue(commentId.ToString("D"), out var comment) ? comment : null;

    private static int Calls(Mock<TableClient> table, string method) => table.Invocations.Count(i => i.Method.Name == method);

    private int CommentWrites() =>
        Calls(h.CommentTable, nameof(TableClient.UpsertEntityAsync)) + Calls(h.CommentTable, nameof(TableClient.AddEntityAsync))
        + Calls(h.CommentTable, nameof(TableClient.UpdateEntityAsync));

    // ---- add

    [TestMethod]
    public async Task AddStoresTheCommentInItsOwnTableAndAnswers201WithLocationBodyAndETag()
    {
        var (activityId, _) = await CreateActivity();
        var eventId = SeedEvent();
        var request = Request(new { text = "  Ran long, cut the second round  ", eventId });

        var result = await h.CommentApi.Add(request, activityId, default);

        var created = Assert.IsExactInstanceOfType<CreatedResult>(result);
        var comment = Assert.IsExactInstanceOfType<ActivityCommentEntity>(created.Value);
        Assert.AreEqual($"/api/activities/{activityId}/comments/{comment.Id}", created.Location);
        Assert.AreNotEqual(Guid.Empty, comment.Id);
        Assert.AreEqual(activityId, comment.ActivityId);
        Assert.AreEqual("Ran long, cut the second round", comment.Text);
        Assert.AreEqual(eventId, comment.EventId);
        var etag = ETagHeader(request);
        Assert.IsNotNull(etag, "POST must answer with the new row's ETag header.");
        Assert.AreEqual(etag, comment.ETag.ToString());

        var stored = Stored(comment.Id);
        Assert.IsNotNull(stored, "The Comment is a row of the ActivityComments table.");
        Assert.AreEqual("Comment", stored.PartitionKey);
        Assert.AreEqual("Ran long, cut the second round", stored.Text);
        // The Activity row and its old Comments field are not touched.
        Assert.IsNull(h.Activities[activityId.ToString("D")].GetString("Comments"));
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
        var comment = ApiResults.Value<ActivityCommentEntity>(result);
        Assert.IsTrue(comment.CreatedUtc >= before && comment.CreatedUtc <= after, $"CreatedUtc {comment.CreatedUtc:O} is not the time of the request.");
        Assert.AreEqual(TimeSpan.Zero, comment.CreatedUtc.Offset);
        Assert.AreNotEqual(sentId, comment.Id);
        Assert.AreEqual(activityId, comment.ActivityId);
        Assert.IsNull(comment.EventId);
        Assert.AreEqual(comment.CreatedUtc, Stored(comment.Id)!.CreatedUtc);
    }

    [TestMethod]
    public async Task AddToAnUnknownActivityIs404AndWritesNothing()
    {
        var result = await h.CommentApi.Add(Request(new { text = "Orphan" }), Guid.NewGuid(), default);

        Assert.IsExactInstanceOfType<NotFoundResult>(result);
        Assert.IsEmpty(h.ActivityComments);
        Assert.AreEqual(0, h.Writes());
    }

    [TestMethod]
    public async Task AddWithAnUnknownEventIs400AndWritesNothing()
    {
        var (activityId, _) = await CreateActivity();

        var result = await h.CommentApi.Add(Request(new { text = "From nowhere", eventId = Guid.NewGuid() }), activityId, default);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(result));
        Assert.AreEqual(EventNotFound, ApiResults.Message(result));
        Assert.IsEmpty(h.ActivityComments);
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
        Assert.ContainsSingle(h.ActivityComments);
        Assert.AreEqual("Keep me", Stored(comment.Id)!.Text);
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
        Assert.AreEqual(2, h.ActivityComments.Count);

        var edit = await h.CommentApi.Update(Request(new { text = over }), activityId, comment.Id, default);
        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(edit));
        Assert.AreEqual(TextTooLong, ApiResults.Message(edit));
        Assert.AreEqual(limit, Stored(comment.Id)!.Text);

        var editOk = await h.CommentApi.Update(Request(new { text = new string('d', 1000) }), activityId, comment.Id, default);
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(editOk));
        Assert.AreEqual(new string('d', 1000), Stored(comment.Id)!.Text);
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
        Assert.AreEqual("Keep me", Assert.ContainsSingle(h.ActivityComments).Value.Text);
        Assert.AreEqual(1, CommentWrites());
    }

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
        var edited = ApiResults.Value<ActivityCommentEntity>(result);
        var stored = Stored(comment.Id)!;
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
        Assert.AreEqual("Mine", Stored(comment.Id)!.Text);
        Assert.ContainsSingle(h.ActivityComments);
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
        Assert.AreEqual("First editor", Stored(comment.Id)!.Text);
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

        Assert.AreEqual("One again", Stored(first.Id)!.Text);
        Assert.AreEqual("Two edited", Stored(second.Id)!.Text);
        Assert.AreEqual("Renamed", h.Activities[activityId.ToString("D")].GetString("Name"));
    }

    // ---- delete

    [TestMethod]
    public async Task DeleteRemovesTheCommentAndARepeatIsAnIdempotent204()
    {
        var (activityId, _) = await CreateActivity();
        var (comment, etag) = await AddComment(activityId, "Wrong remark");
        var (kept, _) = await AddComment(activityId, "Right remark");

        Assert.IsExactInstanceOfType<NoContentResult>(await h.CommentApi.Delete(Bare(etag), activityId, comment.Id, default));
        Assert.IsNull(Stored(comment.Id));
        Assert.AreEqual(kept.Id, Assert.ContainsSingle(await List(activityId)).Id);

        // Gone: a repeat delete is still 204, with or without a tag, and so is an id never used.
        Assert.IsExactInstanceOfType<NoContentResult>(await h.CommentApi.Delete(Bare(etag), activityId, comment.Id, default));
        Assert.IsExactInstanceOfType<NoContentResult>(await h.CommentApi.Delete(Bare(), activityId, comment.Id, default));
        Assert.IsExactInstanceOfType<NoContentResult>(await h.CommentApi.Delete(Bare("*"), activityId, Guid.NewGuid(), default));
        Assert.IsNotNull(Stored(kept.Id));
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
        Assert.AreEqual("Newer", Stored(comment.Id)!.Text);
        Assert.IsExactInstanceOfType<NoContentResult>(await h.CommentApi.Delete(Bare(ETagHeader(edit)), activityId, comment.Id, default));
        Assert.IsNull(Stored(comment.Id));
    }

    [TestMethod]
    public async Task DeleteOfACommentOfAnotherActivityIs204AndDeletesNothing()
    {
        var (owner, _) = await CreateActivity("Owner");
        var (other, _) = await CreateActivity("Other");
        var (comment, _) = await AddComment(owner, "Mine");

        Assert.IsExactInstanceOfType<NoContentResult>(await h.CommentApi.Delete(Bare(), other, comment.Id, default));

        Assert.IsNotNull(Stored(comment.Id));
        Assert.AreEqual(0, Calls(h.CommentTable, nameof(TableClient.DeleteEntityAsync)));
    }

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
        Assert.AreEqual(kept.Id, Assert.ContainsSingle(h.ActivityComments).Value.Id);
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

        Assert.AreEqual(kept.Id, Assert.ContainsSingle(h.ActivityComments).Value.Id);
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
        Assert.IsNotNull(Stored(comment.Id));
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
        Assert.IsNotNull(Stored(comment.Id));
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
        Assert.ContainsSingle(h.ActivityComments);
    }
}
