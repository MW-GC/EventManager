using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using MW_GC.EventManager.API.Functions;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;

namespace MW_GC.EventManager.Api.Tests;

// Issue #101: the anonymous feedback routes, one like per visitor address, the rate limit and the
// admin link and count routes, over in-memory tables behind the real stores.
[TestClass]
public sealed class FeedbackTests
{
    private readonly ConcurrentDictionary<string, TableEntity> _events = new();
    private readonly ConcurrentDictionary<string, TableEntity> _links = new();
    private readonly ConcurrentDictionary<string, TableEntity> _likes = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly EventFunctions _eventApi;
    private readonly FeedbackFunctions _api;

    public FeedbackTests()
    {
        var service = new Mock<TableServiceClient>();
        service.Setup(s => s.GetTableClient("Events")).Returns(TableMocks.Table(_events).Object);
        service.Setup(s => s.GetTableClient(FeedbackStore.LinksTable)).Returns(TableMocks.Table(_links).Object);
        service.Setup(s => s.GetTableClient(FeedbackStore.LikesTable)).Returns(TableMocks.FilteringTable(_likes).Object);
        var events = new TableStore<EventEntity>(service.Object, "Events", "Event");
        _eventApi = new EventFunctions(events, null!, null!, new ListLogger<EventFunctions>());
        _api = new FeedbackFunctions(new FeedbackStore(service.Object), events, new LikeRateLimiter(_time), new ListLogger<FeedbackFunctions>());
    }

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

    // ---- Client address -----------------------------------------------------------------------

    [TestMethod]
    [DataRow("1.2.3.4", "1.2.3.4")]
    [DataRow("1.2.3.4:5678", "1.2.3.4")]
    [DataRow("[::1]:80", "::1")]
    [DataRow("[2001:db8::7]", "2001:db8::7")]
    [DataRow("2001:db8::7", "2001:db8::7")]
    [DataRow("1.2.3.4, 10.0.0.1, 10.0.0.2", "1.2.3.4")]
    [DataRow("1.2.3.4:5678,10.0.0.1:443", "1.2.3.4")]
    [DataRow(" [2001:DB8::7]:443 , 10.0.0.1", "2001:db8::7")]
    [DataRow("::ffff:1.2.3.4", "1.2.3.4")]
    public void ClientIp_ReadsTheFirstForwardedForEntry_WithoutItsPort(string header, string expected)
    {
        var request = TestRequests.Empty();
        request.Headers[ClientIp.ForwardedForHeader] = header;
        Assert.AreEqual(expected, ClientIp.From(request));
    }

    [TestMethod]
    public void ClientIp_SeveralForwardedForHeaderLines_UseTheFirst()
    {
        var request = TestRequests.Empty();
        request.Headers[ClientIp.ForwardedForHeader] = new Microsoft.Extensions.Primitives.StringValues(["1.2.3.4:99", "10.0.0.1"]);
        Assert.AreEqual("1.2.3.4", ClientIp.From(request));
    }

    [TestMethod]
    public void ClientIp_HeaderPrecedence_ClientIpThenAzureClientIpThenForwardedFor()
    {
        var request = TestRequests.Empty();
        request.Headers[ClientIp.ForwardedForHeader] = "203.0.113.50, 10.0.0.1";
        Assert.AreEqual("203.0.113.50", ClientIp.From(request), "X-Forwarded-For alone");

        request.Headers[ClientIp.AzureClientIpHeader] = "198.51.100.20";
        Assert.AreEqual("198.51.100.20", ClientIp.From(request), "X-Azure-ClientIP beats X-Forwarded-For");

        request.Headers[ClientIp.ClientIpHeader] = "192.0.2.10:5678";
        Assert.AreEqual("192.0.2.10", ClientIp.From(request), "CLIENT-IP beats both");
    }

    [TestMethod]
    public void ClientIp_AnUnparseableHeader_CountsAsAbsent_AndTheNextSourceIsUsed()
    {
        var request = TestRequests.Empty();
        request.Headers[ClientIp.ClientIpHeader] = "not-an-address";
        request.Headers[ClientIp.AzureClientIpHeader] = "1.2.3.4:port";
        request.Headers[ClientIp.ForwardedForHeader] = "203.0.113.50";
        Assert.AreEqual("203.0.113.50", ClientIp.From(request));

        request.Headers[ClientIp.ForwardedForHeader] = "unknown";
        Assert.IsNull(ClientIp.From(request), "no usable source at all");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("unknown")]
    [DataRow("1.2.3.4:port")]
    [DataRow("[::1")]
    [DataRow("1.2.3.4:1234567")]
    public void ClientIp_UnusableHeader_IsNull(string header)
    {
        var request = TestRequests.Empty();
        request.Headers[ClientIp.ForwardedForHeader] = header;
        Assert.IsNull(ClientIp.From(request));
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

    // ---- Client address sources (the one place the source order is decided) ------------------

    [TestMethod]
    public void ClientIp_ReadsEachSourceOnItsOwn()
    {
        var request = TestRequests.Empty();
        request.Headers[ClientIp.ClientIpHeader] = "192.0.2.10";
        request.Headers[ClientIp.AzureClientIpHeader] = "198.51.100.20";
        request.Headers[ClientIp.ForwardedForHeader] = "1.2.3.4:5678, 10.0.0.1";

        Assert.AreEqual("192.0.2.10", ClientIp.Read(request, ClientIpSource.ClientIp));
        Assert.AreEqual("198.51.100.20", ClientIp.Read(request, ClientIpSource.AzureClientIp));
        Assert.AreEqual("1.2.3.4", ClientIp.Read(request, ClientIpSource.ForwardedFor));
    }

    [TestMethod]
    public void ClientIp_EachSourceIsNull_WhenItHoldsNothingUsable()
    {
        var request = TestRequests.Empty();
        request.Headers[ClientIp.AzureClientIpHeader] = "unknown";

        Assert.IsNull(ClientIp.Read(request, ClientIpSource.ClientIp));
        Assert.IsNull(ClientIp.Read(request, ClientIpSource.AzureClientIp));
        Assert.IsNull(ClientIp.Read(request, ClientIpSource.ForwardedFor));
    }

    // Header names, not ClientIpSource values: the enum is internal and test methods are public.
    public static IEnumerable<object[]> AddressFormsPerSource =>
        from source in new[] { ClientIp.ClientIpHeader, ClientIp.AzureClientIpHeader, ClientIp.ForwardedForHeader }
        from form in new[]
        {
            new[] { "1.2.3.4", "1.2.3.4" },
            new[] { "1.2.3.4:5678", "1.2.3.4" },
            new[] { "[2001:db8::5]:443", "2001:db8::5" },
            new[] { "2001:db8::5", "2001:db8::5" },
            new[] { "[2001:DB8::5]", "2001:db8::5" },
            new[] { "::ffff:1.2.3.4", "1.2.3.4" },
            new[] { "1.2.3.4\r\n", "1.2.3.4" },
        }
        select new object[] { source, form[0], form[1] };

    [TestMethod]
    [DynamicData(nameof(AddressFormsPerSource))]
    public void ClientIp_EverySource_ParsesIPv4WithPort_BracketedIPv6WithPort_AndBareIPv6(string header, string value, string expected)
    {
        var request = TestRequests.Empty();
        request.Headers[header] = value;
        Assert.AreEqual(expected, ClientIp.Read(request, SourceOf(header)));
        Assert.AreEqual(expected, ClientIp.From(request), "the default order finds it too");
    }

    [TestMethod]
    [DataRow(ClientIp.ClientIpHeader)]
    [DataRow(ClientIp.AzureClientIpHeader)]
    [DataRow(ClientIp.ForwardedForHeader)]
    public void ClientIp_EverySource_TreatsAnUnparseableValueAsAbsent(string header)
    {
        foreach (var value in new[] { "", "unknown", "1.2.3.4:port", "[::1", "1.2.3.4:1234567", "1.2.3.4\r\nX-Evil: 1" })
        {
            var request = TestRequests.Empty();
            request.Headers[header] = value;
            Assert.IsNull(ClientIp.Read(request, SourceOf(header)), $"'{value}'");
        }
    }

    [TestMethod]
    public void ClientIp_TheFirstSourceWithAUsableAddressWins_InTheGivenOrder()
    {
        var request = TestRequests.Empty();
        request.Headers[ClientIp.ClientIpHeader] = "192.0.2.10";
        request.Headers[ClientIp.AzureClientIpHeader] = "198.51.100.20";
        request.Headers[ClientIp.ForwardedForHeader] = "1.2.3.4";

        Assert.AreEqual("1.2.3.4", ClientIp.From(request, [ClientIpSource.ForwardedFor, ClientIpSource.ClientIp]));
        Assert.AreEqual("198.51.100.20", ClientIp.From(request, [ClientIpSource.AzureClientIp, ClientIpSource.ForwardedFor]));

        request.Headers.Remove(ClientIp.AzureClientIpHeader);
        Assert.AreEqual("1.2.3.4", ClientIp.From(request, [ClientIpSource.AzureClientIp, ClientIpSource.ForwardedFor]), "an empty source falls through");
        Assert.IsNull(ClientIp.From(request, []));
    }

    [TestMethod]
    public void ClientIp_DefaultSources_AreClientIpThenAzureClientIpThenForwardedFor_AndNeverTheConnection()
    {
        // Woody's decision (2026-10-11). Changing ClientIp.Sources is the one-line decision; update this with it.
        CollectionAssert.AreEqual(
            new[] { ClientIpSource.ClientIp, ClientIpSource.AzureClientIp, ClientIpSource.ForwardedFor },
            ClientIp.Sources.ToArray());

        var request = TestRequests.Empty();
        request.HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.9");
        Assert.IsNull(ClientIp.From(request), "RemoteIpAddress is never a source");
    }

    private static ClientIpSource SourceOf(string header) => header switch
    {
        ClientIp.ClientIpHeader => ClientIpSource.ClientIp,
        ClientIp.AzureClientIpHeader => ClientIpSource.AzureClientIp,
        ClientIp.ForwardedForHeader => ClientIpSource.ForwardedFor,
        _ => throw new ArgumentOutOfRangeException(nameof(header), header, null),
    };

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

    // ---- Helpers ------------------------------------------------------------------------------

    private static readonly DateTimeOffset EventDate = new(2026, 10, 9, 19, 0, 0, TimeSpan.Zero);

    private async Task<Guid> CreateEvent(string name = "Game night")
    {
        var game = new Game { Id = Guid.NewGuid(), Name = "Alpha" };
        var evt = new EventEntity
        {
            Name = name,
            Date = EventDate,
            Selections = [new Selection { Game = game, Activity = new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Race" } }],
        };
        var result = await _eventApi.SaveCustomized(TestRequests.Json(evt), default);
        Assert.AreEqual(201, ApiResults.Status(result));
        return ApiResults.Value<EventEntity>(result).Id;
    }

    private async Task<string> Link(Guid id) =>
        ApiResults.Value<FeedbackLink>(await _api.GetLink(TestRequests.Empty(), id, default)).Token;

    private async Task<int> Count(Guid id) =>
        ApiResults.Value<EventLikes>(await _api.GetLikes(TestRequests.Empty(), id, default)).Count;

    private static HttpRequest From(string forwardedFor)
    {
        var request = TestRequests.Empty();
        request.Headers[ClientIp.ForwardedForHeader] = forwardedFor;
        return request;
    }

    private static string Json(IActionResult result) =>
        JsonSerializer.Serialize(Assert.IsInstanceOfType<ObjectResult>(result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
