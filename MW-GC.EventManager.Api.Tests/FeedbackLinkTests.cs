using System.Text.Json;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Mvc;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.Shared.Models;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>The feedback link (#101): the admin link route, its token, and the anonymous GET of the Event behind it.</summary>
[TestClass]
public sealed class FeedbackLinkTests : FeedbackTestBase
{
    // ---- Tokens and the admin link route ------------------------------------------------------

    [TestMethod]
    public async Task FeedbackLink_IsCreatedOnce_UrlSafe_AndNotDerivedFromTheEventId()
    {
        var id = await CreateEvent();

        var first = await Link(id);
        var second = await Link(id);

        Assert.AreEqual(first, second, "the token is created once, then reused");
        Assert.AreEqual(FeedbackTokens.Length, first.Length);
        Assert.IsTrue(FeedbackTokens.IsWellFormed(first));
        Assert.DoesNotContain(id.ToString("N"), first);
        Assert.AreNotEqual(first, await Link(await CreateEvent()), "each Event has its own token");
    }

    [TestMethod]
    public async Task FeedbackLink_ForAnUnknownEvent_Is404_AndCreatesNothing()
    {
        Assert.AreEqual(404, ApiResults.Status(await _api.GetLink(TestRequests.Empty(), Guid.NewGuid(), default)));
        Assert.IsEmpty(_links);
    }

    [TestMethod]
    public async Task TokenNeverAppearsInTheAdminEventResponses()
    {
        var id = await CreateEvent();
        var token = await Link(id);

        var list = await _eventApi.GetAll(TestRequests.Empty(), default);
        var one = await _eventApi.Get(TestRequests.Empty(), id, default);

        Assert.DoesNotContain(token, Json(list));
        Assert.DoesNotContain(token, Json(one));
    }

    // ---- Anonymous GET -----------------------------------------------------------------------

    [TestMethod]
    public async Task GetFeedbackEvent_ReturnsOnlyTheNameAndDate()
    {
        var id = await CreateEvent("Game night");
        var token = await Link(id);

        var result = await _api.GetEvent(TestRequests.Empty(), token, default);

        var value = ApiResults.Value<FeedbackEvent>(result);
        Assert.AreEqual("Game night", value.Name);
        Assert.AreEqual(EventDate, value.Date);
        var json = JsonDocument.Parse(Json(result)).RootElement;
        CollectionAssert.AreEquivalent(new[] { "name", "date" }, json.EnumerateObject().Select(p => p.Name).ToArray());
    }

    public static IEnumerable<object[]> BadTokens =>
    [
        ["AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"],      // well formed, unknown
        ["short"],                                           // malformed: length
        ["AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="],      // malformed: padding character
        ["AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/"],      // malformed: not base64url
        [""],
        [Guid.Empty.ToString("D")],
    ];

    [TestMethod]
    [DynamicData(nameof(BadTokens))]
    public async Task UnknownOrMalformedToken_IsTheSameEmpty404_OnBothRoutes(string token)
    {
        await Link(await CreateEvent());

        var get = await _api.GetEvent(TestRequests.Empty(), token, default);
        var like = await _api.Like(From("203.0.113.7"), token, default);

        Assert.IsInstanceOfType<NotFoundResult>(get);
        Assert.IsInstanceOfType<NotFoundResult>(like);
        Assert.IsEmpty(_likes);
    }

    [TestMethod]
    public async Task TokenOfADeletedEvent_Is404_OnBothRoutes()
    {
        var id = await CreateEvent();
        var token = await Link(id);
        await _eventApi.Delete(TestRequests.Empty(), id, default);

        Assert.IsInstanceOfType<NotFoundResult>(await _api.GetEvent(TestRequests.Empty(), token, default));
        Assert.IsInstanceOfType<NotFoundResult>(await _api.Like(From("203.0.113.7"), token, default));
    }

    [TestMethod]
    public async Task ATokenRowTheEventDoesNotName_IsDead()
    {
        var id = await CreateEvent();
        await Link(id);
        // A token row left behind by a lost create race: it points at the Event, the Event names another token.
        var orphan = FeedbackTokens.Create();
        _links[orphan] = new TableEntity(FeedbackStore.TokenPartition, orphan) { [FeedbackStore.EventIdProperty] = id };

        Assert.IsInstanceOfType<NotFoundResult>(await _api.GetEvent(TestRequests.Empty(), orphan, default));
    }
}
