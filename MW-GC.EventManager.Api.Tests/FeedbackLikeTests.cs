using Microsoft.AspNetCore.Mvc;
using MW_GC.EventManager.API.Functions;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>Anonymous likes (#101): one per visitor address per Event, the rate limit, and the admin count.</summary>
[TestClass]
public sealed class FeedbackLikeTests : FeedbackTestBase
{
    // ---- Anonymous like ----------------------------------------------------------------------

    [TestMethod]
    public async Task Like_FirstCounts_SameAddressAgainIsAlreadyLiked_AnotherAddressCounts()
    {
        var id = await CreateEvent();
        var token = await Link(id);

        var first = await _api.Like(From("203.0.113.7"), token, default);
        var again = await _api.Like(From("203.0.113.7"), token, default);
        var other = await _api.Like(From("198.51.100.20"), token, default);

        Assert.AreEqual(200, ApiResults.Status(first));
        Assert.IsFalse(ApiResults.Value<FeedbackLikeResult>(first).AlreadyLiked);
        Assert.AreEqual(200, ApiResults.Status(again));
        Assert.IsTrue(ApiResults.Value<FeedbackLikeResult>(again).AlreadyLiked);
        Assert.AreEqual("{\"alreadyLiked\":true}", Json(again));
        Assert.IsFalse(ApiResults.Value<FeedbackLikeResult>(other).AlreadyLiked);
        Assert.AreEqual(2, await Count(id));
    }

    [TestMethod]
    public async Task Likes_AreCountedPerEvent()
    {
        var a = await CreateEvent();
        var b = await CreateEvent();
        var tokenA = await Link(a);
        var tokenB = await Link(b);

        await _api.Like(From("203.0.113.7"), tokenA, default);
        var onB = await _api.Like(From("203.0.113.7"), tokenB, default);

        Assert.IsFalse(ApiResults.Value<FeedbackLikeResult>(onB).AlreadyLiked, "one like per address PER EVENT");
        Assert.AreEqual(1, await Count(a));
        Assert.AreEqual(1, await Count(b));
    }

    [TestMethod]
    public async Task ConcurrentLikesFromOneAddress_CountOnce()
    {
        var id = await CreateEvent();
        var token = await Link(id);
        // Eight parallel requests from one address (under the limit of ten), started together.
        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            start.Wait();
            return await _api.Like(From("203.0.113.7"), token, default);
        })).ToList();
        start.Set();
        var results = await Task.WhenAll(tasks);

        Assert.AreEqual(1, results.Count(r => !ApiResults.Value<FeedbackLikeResult>(r).AlreadyLiked));
        Assert.AreEqual(7, results.Count(r => ApiResults.Value<FeedbackLikeResult>(r).AlreadyLiked));
        Assert.AreEqual(1, await Count(id));
    }

    [TestMethod]
    public async Task StoredRows_HoldNoRawAddress()
    {
        var id = await CreateEvent();
        var token = await Link(id);
        string[] addresses = ["203.0.113.7", "2001:db8::1", "198.51.100.20"];
        foreach (var address in addresses)
            await _api.Like(From(address), token, default);

        var like = _likes.Values.ToList();
        Assert.HasCount(3, like);
        foreach (var row in like)
        {
            // PartitionKey = the Event id, RowKey = the salted hash, nothing else written.
            Assert.AreEqual(id.ToString("D"), row.PartitionKey);
            Assert.MatchesRegex("^[0-9a-f]{64}$", row.RowKey);
            CollectionAssert.AreEquivalent(new[] { "PartitionKey", "RowKey", "odata.etag" }, row.Keys.ToArray(),
                string.Join(",", row.Keys));
        }
        var everything = string.Join("|", _likes.Values.Concat(_links.Values).SelectMany(r => r.Select(kv => $"{kv.Key}={kv.Value}")));
        foreach (var address in addresses)
            Assert.DoesNotContain(address, everything);
    }

    [TestMethod]
    public void LikeKey_IsTheTokenSaltedHash()
    {
        var token = FeedbackTokens.Create();
        Assert.AreEqual(FeedbackTokens.LikeKey(token, "203.0.113.7"), FeedbackTokens.LikeKey(token, "203.0.113.7"));
        Assert.AreNotEqual(FeedbackTokens.LikeKey(token, "203.0.113.7"), FeedbackTokens.LikeKey(token, "203.0.113.8"));
        Assert.AreNotEqual(FeedbackTokens.LikeKey(token, "203.0.113.7"), FeedbackTokens.LikeKey(FeedbackTokens.Create(), "203.0.113.7"),
            "another Event's token gives another key for the same address");
    }

    [TestMethod]
    public async Task Like_WithoutAnyClientAddress_Is400_AndStoresNothing()
    {
        var token = await Link(await CreateEvent());

        var result = await _api.Like(TestRequests.Empty(), token, default);

        Assert.AreEqual(400, ApiResults.Status(result));
        Assert.AreEqual(FeedbackFunctions.MissingClientIpMessage, ApiResults.Message(result));
        Assert.IsEmpty(_likes);
    }

    [TestMethod]
    [DataRow("short")]
    [DataRow("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [DataRow("")]
    public async Task Like_MalformedToken_WithoutAnyClientAddress_IsTheEmpty404(string token)
    {
        await Link(await CreateEvent());

        var result = await _api.Like(TestRequests.Empty(), token, default);

        Assert.IsInstanceOfType<NotFoundResult>(result);
        Assert.IsEmpty(_likes);
    }

    [TestMethod]
    public async Task Like_UsesClientIpHeaderFirst_SoAnotherForwardedForDoesNotMakeANewVisitor()
    {
        var id = await CreateEvent();
        var token = await Link(id);

        var first = TestRequests.Empty();
        first.Headers[ClientIp.ClientIpHeader] = "192.0.2.10:5678";
        first.Headers[ClientIp.ForwardedForHeader] = "203.0.113.1";
        var second = TestRequests.Empty();
        second.Headers[ClientIp.ClientIpHeader] = "192.0.2.10";
        second.Headers[ClientIp.ForwardedForHeader] = "203.0.113.2";

        Assert.IsFalse(ApiResults.Value<FeedbackLikeResult>(await _api.Like(first, token, default)).AlreadyLiked);
        Assert.IsTrue(ApiResults.Value<FeedbackLikeResult>(await _api.Like(second, token, default)).AlreadyLiked);
        Assert.AreEqual(1, await Count(id));
    }

    [TestMethod]
    public async Task AddressWithAndWithoutPort_IsOneVisitor()
    {
        var id = await CreateEvent();
        var token = await Link(id);

        await _api.Like(From("1.2.3.4:5678"), token, default);
        var again = await _api.Like(From("1.2.3.4, 10.0.0.1"), token, default);

        Assert.IsTrue(ApiResults.Value<FeedbackLikeResult>(again).AlreadyLiked);
        Assert.AreEqual(1, await Count(id));
    }

    // ---- Rate limit ---------------------------------------------------------------------------

    [TestMethod]
    public async Task Like_EleventhRequestInAMinute_Is429_UntilTheWindowEnds()
    {
        var token = await Link(await CreateEvent());
        for (var i = 0; i < LikeRateLimiter.Limit; i++)
            Assert.AreEqual(200, ApiResults.Status(await _api.Like(From("203.0.113.7"), token, default)), $"request {i + 1}");

        _time.Advance(TimeSpan.FromSeconds(20));
        var request = From("203.0.113.7");
        var limited = await _api.Like(request, token, default);

        Assert.AreEqual(429, ApiResults.Status(limited));
        Assert.AreEqual("40", request.HttpContext.Response.Headers.RetryAfter.ToString());
        Assert.AreEqual(200, ApiResults.Status(await _api.Like(From("198.51.100.20"), token, default)), "another address is not limited");

        _time.Advance(TimeSpan.FromSeconds(40));
        Assert.AreEqual(200, ApiResults.Status(await _api.Like(From("203.0.113.7"), token, default)), "a new window");
    }

    [TestMethod]
    public async Task RateLimit_CountsBadTokensToo_SoA429SaysNothingAboutATokenGuess()
    {
        var token = await Link(await CreateEvent());
        for (var i = 0; i < LikeRateLimiter.Limit; i++)
            Assert.AreEqual(404, ApiResults.Status(await _api.Like(From("203.0.113.7"), FeedbackTokens.Create(), default)));

        Assert.AreEqual(429, ApiResults.Status(await _api.Like(From("203.0.113.7"), FeedbackTokens.Create(), default)));
        Assert.AreEqual(429, ApiResults.Status(await _api.Like(From("203.0.113.7"), token, default)));
    }

    [TestMethod]
    public async Task MalformedTokens_AreRefusedBeforeTheRateLimit()
    {
        var token = await Link(await CreateEvent());
        for (var i = 0; i < LikeRateLimiter.Limit * 2; i++)
            Assert.IsInstanceOfType<NotFoundResult>(await _api.Like(From("203.0.113.7"), $"guess{i}", default));

        Assert.AreEqual(200, ApiResults.Status(await _api.Like(From("203.0.113.7"), token, default)));
    }

    // ---- Admin count -------------------------------------------------------------------------

    [TestMethod]
    public async Task AdminCount_MatchesTheLikes_AndIs404ForAnUnknownEvent()
    {
        var id = await CreateEvent();
        Assert.AreEqual(0, await Count(id));
        var token = await Link(id);
        await _api.Like(From("203.0.113.7"), token, default);
        await _api.Like(From("198.51.100.20"), token, default);

        Assert.AreEqual(2, await Count(id));
        Assert.AreEqual(404, ApiResults.Status(await _api.GetLikes(TestRequests.Empty(), Guid.NewGuid(), default)));
    }

    [TestMethod]
    public async Task AdminPutOfTheEvent_DoesNotChangeTheCountOrTheLink()
    {
        var id = await CreateEvent();
        var token = await Link(id);
        await _api.Like(From("203.0.113.7"), token, default);
        await _api.Like(From("198.51.100.20"), token, default);

        var stored = ApiResults.Value<EventEntity>(await _eventApi.Get(TestRequests.Empty(), id, default));
        stored.Name = "Renamed night";
        var put = await _eventApi.Update(TestRequests.Json(stored), id, default);

        Assert.AreEqual(200, ApiResults.Status(put));
        Assert.AreEqual(2, await Count(id));
        Assert.AreEqual(token, await Link(id));
        Assert.AreEqual("Renamed night", ApiResults.Value<FeedbackEvent>(await _api.GetEvent(TestRequests.Empty(), token, default)).Name);
    }
}
