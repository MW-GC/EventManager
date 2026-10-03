using System.Net;
using System.Text;
using Microsoft.AspNetCore.Components;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Tests;

// The Web HttpClient's handler that sends a signed-out user to sign in when an API call shows
// the session expired, and leaves every other answer, and an outage, to the page as before.
[TestClass]
public class SessionExpiredHandlerTests
{
    private const string Origin = "https://example.test";
    private const string SignInFromGames = "/.auth/login/aad?post_login_redirect_uri=%2Fgames";

    private RecordingNavigationManager _navigation = null!;
    private FakeInnerHandler _inner = null!;
    private HttpClient _client = null!;

    [TestInitialize]
    public void SetUp()
    {
        _navigation = new RecordingNavigationManager($"{Origin}/", $"{Origin}/games");
        _inner = new FakeInnerHandler();
        _client = new HttpClient(new SessionExpiredHandler(_navigation) { InnerHandler = _inner })
        {
            BaseAddress = new Uri($"{Origin}/")
        };
    }

    [TestCleanup]
    public void TearDown() => _client.Dispose();

    [TestMethod]
    public async Task UnauthorizedOnAnApiCallSendsTheUserToSignInOnce()
    {
        _inner.Api = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);

        using var response = await _client.GetAsync("api/games");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, "the original response is returned");
        AssertNavigatedToSignInOnce(SignInFromGames);
        Assert.AreEqual(0, _inner.ProbeCount, "a 401 needs no /.auth/me probe");
    }

    [TestMethod]
    public async Task SignInReturnsToTheCurrentPathAndQuery()
    {
        _navigation = new RecordingNavigationManager($"{Origin}/", $"{Origin}/activities?game=1");
        using var client = new HttpClient(new SessionExpiredHandler(_navigation) { InnerHandler = _inner }) { BaseAddress = new Uri($"{Origin}/") };
        _inner.Api = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);

        using var response = await client.GetAsync("api/activities");

        AssertNavigatedToSignInOnce(ApiResult.SignInUrl("/activities?game=1"));
    }

    [TestMethod]
    public async Task ApiCallThatFollowedARedirectToTheSignInRouteSendsTheUserToSignIn()
    {
        _inner.Api = request =>
        {
            // The browser followed SWA's 302: the final URL is the sign-in route.
            request.RequestUri = new Uri($"{Origin}/.auth/login/aad?post_login_redirect_uri=.referrer");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        };

        using var response = await _client.GetAsync("api/games");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        AssertNavigatedToSignInOnce(SignInFromGames);
        Assert.AreEqual(0, _inner.ProbeCount, "a followed redirect needs no /.auth/me probe");
    }

    [TestMethod]
    public async Task ApiCallAnsweredWithTheSignInPageAsHtmlSendsTheUserToSignIn()
    {
        _inner.Api = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>Sign in</body></html>", Encoding.UTF8, "text/html")
        };

        using var response = await _client.GetAsync("api/games");

        Assert.AreEqual("text/html", response.Content.Headers.ContentType?.MediaType, "the original response is returned");
        AssertNavigatedToSignInOnce(SignInFromGames);
    }

    [TestMethod]
    public async Task OpaqueFailureWhileSignedOutSendsTheUserToSignInAndRethrows()
    {
        var failure = new HttpRequestException("TypeError: Failed to fetch");
        _inner.Api = _ => throw failure;
        _inner.Me = () => Json("{\"clientPrincipal\":null}");

        var thrown = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => _client.GetAsync("api/games"));

        Assert.AreSame(failure, thrown, "the original failure is rethrown unchanged");
        Assert.AreEqual(1, _inner.ProbeCount);
        Assert.AreEqual($"{Origin}/.auth/me", _inner.ProbeUris.Single());
        AssertNavigatedToSignInOnce(SignInFromGames);
    }

    [TestMethod]
    public async Task OpaqueFailureWhenTheSignInCheckAlsoFailsIsAnOutageAndDoesNotNavigate()
    {
        var failure = new HttpRequestException("TypeError: Failed to fetch");
        _inner.Api = _ => throw failure;
        _inner.Me = () => throw new HttpRequestException("TypeError: Failed to fetch");

        var thrown = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => _client.GetAsync("api/games"));

        Assert.AreSame(failure, thrown);
        Assert.AreEqual(1, _inner.ProbeCount);
        Assert.IsEmpty(_navigation.Navigations);
        Assert.AreEqual(ApiResult.UnreachableMessage, ApiResult.FromException(thrown), "the page still shows the unreachable message");
    }

    [TestMethod]
    public async Task OpaqueFailureWhenTheSignInCheckAnswersAnErrorStatusDoesNotNavigate()
    {
        var failure = new HttpRequestException("TypeError: Failed to fetch");
        _inner.Api = _ => throw failure;
        _inner.Me = () => new HttpResponseMessage(HttpStatusCode.BadGateway);

        var thrown = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => _client.GetAsync("api/games"));

        Assert.AreSame(failure, thrown);
        Assert.IsEmpty(_navigation.Navigations);
    }

    [TestMethod]
    public async Task OpaqueFailureWhileStillSignedInDoesNotNavigate()
    {
        var failure = new HttpRequestException("TypeError: Failed to fetch");
        _inner.Api = _ => throw failure;
        _inner.Me = () => Json("{\"clientPrincipal\":{\"userId\":\"u\",\"userRoles\":[\"anonymous\",\"authenticated\",\"admin\"]}}");

        var thrown = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => _client.GetAsync("api/games"));

        Assert.AreSame(failure, thrown);
        Assert.AreEqual(1, _inner.ProbeCount);
        Assert.IsEmpty(_navigation.Navigations);
    }

    [TestMethod]
    public async Task OpaqueFailureWhenTheSignInCheckIsNotJsonDoesNotNavigate()
    {
        var failure = new HttpRequestException("TypeError: Failed to fetch");
        _inner.Api = _ => throw failure;
        _inner.Me = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html></html>", Encoding.UTF8, "text/html") };

        var thrown = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => _client.GetAsync("api/games"));

        Assert.AreSame(failure, thrown);
        Assert.IsEmpty(_navigation.Navigations);
    }

    [TestMethod]
    public async Task TimeoutIsNotTreatedAsASignOut()
    {
        _inner.Api = _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");

        await Assert.ThrowsAsync<OperationCanceledException>(() => _client.GetAsync("api/games"));

        Assert.AreEqual(0, _inner.ProbeCount);
        Assert.IsEmpty(_navigation.Navigations);
    }

    [TestMethod]
    public async Task NonApiRequestIsPassedThroughUntouched()
    {
        var original = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        _inner.Other = _ => original;

        using var response = await _client.GetAsync("appsettings.json");

        Assert.AreSame(original, response);
        Assert.AreEqual(1, _inner.OtherCount);
        Assert.IsEmpty(_navigation.Navigations);
    }

    [TestMethod]
    public async Task NonApiTransportFailureIsNotProbed()
    {
        var failure = new HttpRequestException("TypeError: Failed to fetch");
        _inner.Other = _ => throw failure;

        var thrown = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => _client.GetAsync("css/app.css"));

        Assert.AreSame(failure, thrown);
        Assert.AreEqual(0, _inner.ProbeCount);
        Assert.IsEmpty(_navigation.Navigations);
    }

    [TestMethod]
    public async Task TwoFailingCallsNavigateOnlyOnce()
    {
        _inner.Api = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);

        using var first = await _client.GetAsync("api/games");
        using var second = await _client.GetAsync("api/themes");

        AssertNavigatedToSignInOnce(SignInFromGames);
    }

    [TestMethod]
    public async Task OpaqueFailureAfterTheNavigationIsNotProbedAgain()
    {
        _inner.Api = _ => throw new HttpRequestException("TypeError: Failed to fetch");
        _inner.Me = () => Json("{\"clientPrincipal\":null}");

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => _client.GetAsync("api/games"));
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => _client.GetAsync("api/themes"));

        Assert.AreEqual(1, _inner.ProbeCount);
        AssertNavigatedToSignInOnce(SignInFromGames);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.NotFound)]
    [DataRow(HttpStatusCode.InternalServerError)]
    [DataRow(HttpStatusCode.Forbidden)]
    [DataRow(HttpStatusCode.BadRequest)]
    public async Task OtherApiFailureStatusIsPassedThroughUntouched(HttpStatusCode status)
    {
        var original = new HttpResponseMessage(status) { Content = new StringContent("Name is required.") };
        _inner.Api = _ => original;

        using var response = await _client.GetAsync("api/games");

        Assert.AreSame(original, response);
        Assert.AreEqual(0, _inner.ProbeCount);
        Assert.IsEmpty(_navigation.Navigations);
    }

    [TestMethod]
    public async Task SuccessfulApiJsonIsPassedThroughUntouched()
    {
        var original = Json("[]");
        _inner.Api = _ => original;

        using var response = await _client.GetAsync("api/games");

        Assert.AreSame(original, response);
        Assert.IsEmpty(_navigation.Navigations);
    }

    private void AssertNavigatedToSignInOnce(string expectedUrl)
    {
        Assert.HasCount(1, _navigation.Navigations);
        var (uri, forceLoad) = _navigation.Navigations[0];
        Assert.AreEqual($"{Origin}{expectedUrl}", uri);
        Assert.IsTrue(forceLoad, "a forced load, so the Blazor router does not swallow the /.auth route");
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // Answers /api/* with Api, /.auth/me with Me and anything else with Other, and counts the calls.
    private sealed class FakeInnerHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Api { get; set; } = _ => Json("[]");
        public Func<HttpResponseMessage> Me { get; set; } = () => Json("{\"clientPrincipal\":null}");
        public Func<HttpRequestMessage, HttpResponseMessage> Other { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        public List<string> ProbeUris { get; } = [];
        public int ProbeCount => ProbeUris.Count;
        public int OtherCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/api/", StringComparison.Ordinal)) return Task.FromResult(Api(request));
            if (path == "/.auth/me")
            {
                ProbeUris.Add(request.RequestUri.ToString());
                return Task.FromResult(Me());
            }
            OtherCount++;
            return Task.FromResult(Other(request));
        }
    }

    // A NavigationManager that records each navigation instead of leaving the page.
    private sealed class RecordingNavigationManager : NavigationManager
    {
        public RecordingNavigationManager(string baseUri, string uri) => Initialize(baseUri, uri);

        public List<(string Uri, bool ForceLoad)> Navigations { get; } = [];

        protected override void NavigateToCore(string uri, NavigationOptions options) =>
            Navigations.Add((ToAbsoluteUri(uri).ToString(), options.ForceLoad));
    }
}
