using System.Net;
using System.Net.Http.Json;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Tests;

// #131: the Web client of an Activity's Comments. Each request is pinned here: verb, path, If-Match and
// body. A PUT and a DELETE send that Comment's own eTag, the weak tag exactly as read; nothing throws for
// an API failure.
[TestClass]
public sealed class CommentClientTests
{
    // Azure Table Storage tags are weak and carry quotes; they must reach If-Match as read.
    private const string TagAsRead = "W/\"datetime'2026-11-20T10%3A00%3A00.0000000Z'\"";
    private const string NewTag = "W/\"datetime'2026-11-20T11%3A00%3A00.0000000Z'\"";
    private static readonly Guid ActivityId = Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");
    private static readonly Guid CommentId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset Added = new(2025, 3, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly string ListPath = $"/api/activities/{ActivityId}/comments";
    private static readonly string OnePath = $"/api/activities/{ActivityId}/comments/{CommentId}";

    private readonly StubApiHandler api = new();

    private CommentClient Client() => new(api.Client());

    [TestMethod]
    public async Task GetAll_SendsGet_AndReadsTheListWithEachCommentsTag()
    {
        api.Json(HttpMethod.Get, ListPath, new[] { Body(CommentId, "Ran long", Added, TagAsRead) });

        var result = await Client().GetAllAsync(ActivityId);

        Assert.IsNull(result.Error);
        var comment = Assert.ContainsSingle(result.Comments!);
        Assert.AreEqual(CommentId, comment.Id);
        Assert.AreEqual("Ran long", comment.Text);
        Assert.AreEqual(Added, comment.CreatedUtc);
        Assert.AreEqual(TagAsRead, comment.ETag.ToString());
        var request = Assert.ContainsSingle(api.Requests);
        Assert.AreEqual(HttpMethod.Get, request.Method);
        Assert.AreEqual(ListPath, request.Path);
    }

    [TestMethod]
    public async Task Add_SendsPostWithOnlyTheText_NoIfMatch_AndReadsTheReturnedComment()
    {
        api.On(HttpMethod.Post, ListPath, () => Response(HttpStatusCode.Created, CommentId, "Ran long", TagAsRead));

        var result = await Client().AddAsync(ActivityId, "Ran long");

        Assert.AreEqual("{\"text\":\"Ran long\"}", Assert.ContainsSingle(api.Bodies(HttpMethod.Post, ListPath)));
        Assert.IsNull(Assert.ContainsSingle(api.IfMatches(HttpMethod.Post, ListPath)));
        var stored = Assert.IsInstanceOfType<CommentResult.Stored>(result).Comment!;
        Assert.AreEqual(CommentId, stored.Id);
        Assert.AreEqual(TagAsRead, stored.ETag.ToString());
    }

    // #132: a Comment written from an Event's details names that Event; the API checks it exists.
    [TestMethod]
    public async Task Add_WithAnEventId_SendsTheTextAndTheEventId()
    {
        var eventId = Guid.Parse("99999999-8888-7777-6666-555555555555");
        api.On(HttpMethod.Post, ListPath, () => Response(HttpStatusCode.Created, CommentId, "Ran long", TagAsRead));

        var result = await Client().AddAsync(ActivityId, "Ran long", eventId);

        Assert.AreEqual("{\"text\":\"Ran long\",\"eventId\":\"99999999-8888-7777-6666-555555555555\"}",
            Assert.ContainsSingle(api.Bodies(HttpMethod.Post, ListPath)));
        Assert.IsNull(Assert.ContainsSingle(api.IfMatches(HttpMethod.Post, ListPath)));
        Assert.AreEqual(CommentId, Assert.IsInstanceOfType<CommentResult.Stored>(result).Comment!.Id);
    }

    [TestMethod]
    public async Task Update_SendsPutWithOnlyTheText_AndTheWeakTagAsReadAsIfMatch()
    {
        api.On(HttpMethod.Put, OnePath, () => Response(HttpStatusCode.OK, CommentId, "Ran longer", NewTag));

        var result = await Client().UpdateAsync(ActivityId, CommentId, "Ran longer", TagAsRead);

        Assert.AreEqual("{\"text\":\"Ran longer\"}", Assert.ContainsSingle(api.Bodies(HttpMethod.Put, OnePath)));
        Assert.AreEqual(TagAsRead, Assert.ContainsSingle(api.IfMatches(HttpMethod.Put, OnePath)));
        var stored = Assert.IsInstanceOfType<CommentResult.Stored>(result).Comment!;
        Assert.AreEqual("Ran longer", stored.Text);
        Assert.AreEqual(NewTag, stored.ETag.ToString());
    }

    [TestMethod]
    public async Task Delete_SendsDeleteWithNoBody_AndTheTagAsIfMatch()
    {
        api.Status(HttpMethod.Delete, OnePath, HttpStatusCode.NoContent);

        var result = await Client().DeleteAsync(ActivityId, CommentId, TagAsRead);

        var request = Assert.ContainsSingle(api.Requests);
        Assert.AreEqual(HttpMethod.Delete, request.Method);
        Assert.AreEqual(OnePath, request.Path);
        Assert.IsNull(request.Body);
        Assert.AreEqual(TagAsRead, Assert.ContainsSingle(api.IfMatches(HttpMethod.Delete, OnePath)));
        Assert.IsNull(Assert.IsInstanceOfType<CommentResult.Stored>(result).Comment);
    }

    [TestMethod]
    [DataRow("PUT", HttpStatusCode.Conflict)]
    [DataRow("DELETE", HttpStatusCode.Conflict)]
    [DataRow("PUT", HttpStatusCode.NotFound)]
    [DataRow("DELETE", HttpStatusCode.NotFound)]
    public async Task AConflictOrNotFound_IsItsOwnOutcome_NotThrown(string verb, HttpStatusCode status)
    {
        var method = new HttpMethod(verb);
        api.Status(method, OnePath, status, "The request conflicts with the stored data. Reload and try again.");

        var result = verb == "PUT"
            ? await Client().UpdateAsync(ActivityId, CommentId, "Ran longer", TagAsRead)
            : await Client().DeleteAsync(ActivityId, CommentId, TagAsRead);

        // A result is exactly one outcome: a 409 is Stale and nothing else, a 404 NotFound and nothing else.
        Assert.AreEqual(status == HttpStatusCode.Conflict ? typeof(CommentResult.Stale) : typeof(CommentResult.NotFound), result.GetType());
    }

    [TestMethod]
    public async Task NoCallThrows_WhenTheApiIsUnreachable()
    {
        api.Offline(HttpMethod.Get, ListPath).Offline(HttpMethod.Post, ListPath)
           .Offline(HttpMethod.Put, OnePath).Offline(HttpMethod.Delete, OnePath);
        const string unreachable = "Could not reach the server. Check the connection and try again.";

        Assert.AreEqual(unreachable, (await Client().GetAllAsync(ActivityId)).Error);
        Assert.AreEqual(unreachable, Assert.IsInstanceOfType<CommentResult.Failed>(await Client().AddAsync(ActivityId, "x")).Error);
        Assert.AreEqual(unreachable, Assert.IsInstanceOfType<CommentResult.Failed>(await Client().UpdateAsync(ActivityId, CommentId, "x", TagAsRead)).Error);
        Assert.AreEqual(unreachable, Assert.IsInstanceOfType<CommentResult.Failed>(await Client().DeleteAsync(ActivityId, CommentId, TagAsRead)).Error);
        Assert.HasCount(4, api.Requests);
    }

    // The API's JSON for one Comment: the eTag string in the body, as the API sends it.
    private static object Body(Guid id, string text, DateTimeOffset added, string eTag) =>
        new { id, activityId = ActivityId, text, createdUtc = added, eventId = (Guid?)null, eTag };

    private static HttpResponseMessage Response(HttpStatusCode status, Guid id, string text, string eTag)
    {
        var response = new HttpResponseMessage(status) { Content = JsonContent.Create(Body(id, text, Added, eTag)) };
        response.Headers.TryAddWithoutValidation("ETag", eTag);
        return response;
    }
}
