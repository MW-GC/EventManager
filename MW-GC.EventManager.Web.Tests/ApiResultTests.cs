using System.Net;
using System.Text;
using System.Text.Json;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Tests;

// The shared helper's message mapping for each status class, transport errors and timeouts.
[TestClass]
public class ApiResultTests
{
    private static HttpResponseMessage Response(HttpStatusCode status, string? body = null, string mediaType = "text/plain") =>
        new(status) { Content = body is null ? new StringContent(string.Empty) : new StringContent(body, Encoding.UTF8, mediaType) };

    [TestMethod]
    [DataRow(HttpStatusCode.OK)]
    [DataRow(HttpStatusCode.Created)]
    [DataRow(HttpStatusCode.NoContent)]
    public async Task SuccessHasNoMessage(HttpStatusCode status)
    {
        using var response = Response(status, "ignored");
        Assert.IsNull(await ApiResult.ErrorAsync(response));
    }

    [TestMethod]
    public async Task SuccessThatIsTheSignInPageIsNotASuccess()
    {
        // SWA redirects an expired session to its sign-in page; fetch follows and returns HTML with 200.
        using var response = Response(HttpStatusCode.OK, "<html><body>Sign in</body></html>", "text/html");
        Assert.AreEqual("Your session expired. Sign in again.", await ApiResult.ErrorAsync(response));
        using var json = Response(HttpStatusCode.OK, "{}", "application/json");
        Assert.IsNull(await ApiResult.ErrorAsync(json));
    }

    [TestMethod]
    public async Task UnauthorizedSaysTheSessionExpired()
    {
        using var response = Response(HttpStatusCode.Unauthorized, "Some server text");
        var message = await ApiResult.ErrorAsync(response);
        Assert.AreEqual("Your session expired. Sign in again.", message);
        Assert.IsTrue(ApiResult.IsSessionExpired(message));
    }

    [TestMethod]
    public void SignInUrlReturnsToTheCurrentPath()
    {
        Assert.AreEqual("/.auth/login/aad?post_login_redirect_uri=%2Fgames", ApiResult.SignInUrl("/games"));
        Assert.AreEqual("/.auth/login/aad?post_login_redirect_uri=%2F", ApiResult.SignInUrl(""));
        Assert.IsFalse(ApiResult.IsSessionExpired("Not found. It may have been deleted."));
        Assert.IsFalse(ApiResult.IsSessionExpired(null));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.BadRequest)]
    [DataRow(HttpStatusCode.Conflict)]
    [DataRow(HttpStatusCode.RequestEntityTooLarge)]
    [DataRow(HttpStatusCode.UnsupportedMediaType)]
    [DataRow(HttpStatusCode.UnprocessableEntity)]
    public async Task RejectedRequestShowsTheApisShortPlainText(HttpStatusCode status)
    {
        using var response = Response(status, "  Select between 1 and 5 activities.  ");
        Assert.AreEqual("Select between 1 and 5 activities.", await ApiResult.ErrorAsync(response));
    }

    [TestMethod]
    public async Task RejectedRequestShowsAJsonStringBody()
    {
        // BadRequestObjectResult(string) is serialized as a JSON string by the Functions host.
        using var response = Response(HttpStatusCode.BadRequest, JsonSerializer.Serialize("Event has no selections."), "application/json");
        Assert.AreEqual("Event has no selections.", await ApiResult.ErrorAsync(response));
    }

    [TestMethod]
    public async Task RejectedRequestShowsAProblemDetailsTitle()
    {
        const string problem = "{\"type\":\"https://tools.ietf.org/html/rfc9110#section-15.5.1\",\"title\":\"One or more validation errors occurred.\",\"status\":400}";
        using var response = Response(HttpStatusCode.BadRequest, problem, "application/problem+json");
        Assert.AreEqual("One or more validation errors occurred.", await ApiResult.ErrorAsync(response));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.BadRequest, null)]
    [DataRow(HttpStatusCode.BadRequest, "   ")]
    [DataRow(HttpStatusCode.Conflict, "<html><body>Conflict</body></html>")]
    [DataRow(HttpStatusCode.UnprocessableEntity, "{\"errors\":{\"Name\":[\"Required\"]}}")]
    [DataRow(HttpStatusCode.BadRequest, "{ not json")]
    [DataRow(HttpStatusCode.BadRequest, "[1,2]")]
    public async Task RejectedRequestWithoutAUsableBodyGetsTheGenericMessage(HttpStatusCode status, string? body)
    {
        using var response = Response(status, body);
        Assert.AreEqual($"The request was not accepted (HTTP {(int)status}).", await ApiResult.ErrorAsync(response));
    }

    [TestMethod]
    public async Task RejectedRequestWithALongBodyGetsTheGenericMessage()
    {
        using var response = Response(HttpStatusCode.BadRequest, new string('x', 301));
        Assert.AreEqual("The request was not accepted (HTTP 400).", await ApiResult.ErrorAsync(response));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden)]
    [DataRow(HttpStatusCode.MethodNotAllowed)]
    [DataRow((HttpStatusCode)429)]
    public async Task OtherClientErrorsDoNotEchoTheBody(HttpStatusCode status)
    {
        using var response = Response(status, "internal detail");
        Assert.AreEqual($"The request was not accepted (HTTP {(int)status}).", await ApiResult.ErrorAsync(response));
    }

    [TestMethod]
    public async Task NotFoundSaysItMayHaveBeenDeleted()
    {
        using var response = Response(HttpStatusCode.NotFound, "Game 123 not found");
        Assert.AreEqual("Not found. It may have been deleted.", await ApiResult.ErrorAsync(response));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.InternalServerError)]
    [DataRow(HttpStatusCode.BadGateway)]
    [DataRow(HttpStatusCode.ServiceUnavailable)]
    [DataRow(HttpStatusCode.GatewayTimeout)]
    public async Task ServerErrorNamesTheCodeAndNeverShowsTheBody(HttpStatusCode status)
    {
        using var response = Response(status, "System.InvalidOperationException: boom\n   at Stack.Trace()");
        var message = await ApiResult.ErrorAsync(response);
        Assert.AreEqual($"The server had a problem (HTTP {(int)status}). Try again.", message);
        Assert.DoesNotContain("Exception", message!); // Non-null: the exact text was asserted above.
    }

    [TestMethod]
    public void TransportErrorAndTimeoutSayTheServerCouldNotBeReached()
    {
        const string expected = "Could not reach the server. Check the connection and try again.";
        Assert.AreEqual(expected, ApiResult.FromException(new HttpRequestException("No such host is known (api.example)")));
        Assert.AreEqual(expected, ApiResult.FromException(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing.")));
        Assert.AreEqual(expected, ApiResult.FromException(new OperationCanceledException()));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized, "Your session expired. Sign in again.")]
    [DataRow(HttpStatusCode.NotFound, "Not found. It may have been deleted.")]
    [DataRow(HttpStatusCode.InternalServerError, "The server had a problem (HTTP 500). Try again.")]
    [DataRow(HttpStatusCode.BadRequest, "The request was not accepted (HTTP 400).")]
    public void LoadFailureWithAStatusMapsLikeAResponse(HttpStatusCode status, string expected)
    {
        // GetFromJsonAsync throws HttpRequestException with StatusCode set for a non-success response.
        Assert.AreEqual(expected, ApiResult.FromException(new HttpRequestException("Response status code does not indicate success", null, status)));
    }

    [TestMethod]
    public void UnreadableBodyNeverShowsTheExceptionText()
    {
        var message = ApiResult.FromException(new JsonException("'<' is an invalid start of a value. Path: $ | LineNumber: 0"));
        Assert.AreEqual(ApiResult.UnreadableMessage, message);
        Assert.DoesNotContain("LineNumber", message);
    }

    [TestMethod]
    public async Task LoadAsyncReturnsTheValueOrTheMessageAndNeverThrows()
    {
        var ok = await ApiResult.LoadAsync(() => Task.FromResult<List<int>?>([1, 2]));
        Assert.AreSequenceEqual([1, 2], ok.Value);
        Assert.IsNull(ok.Error);

        var failed = await ApiResult.LoadAsync<List<int>>(() => Task.FromException<List<int>?>(new HttpRequestException("Offline")));
        Assert.IsNull(failed.Value);
        Assert.AreEqual(ApiResult.UnreachableMessage, failed.Error);

        var timedOut = await ApiResult.LoadAsync<List<int>>(() => Task.FromException<List<int>?>(new TaskCanceledException()));
        Assert.AreEqual(ApiResult.UnreachableMessage, timedOut.Error);
    }

    [TestMethod]
    public async Task LoadAsyncDoesNotSwallowProgrammingErrors()
    {
        await Assert.ThrowsExactlyAsync<NullReferenceException>(() =>
            ApiResult.LoadAsync<List<int>>(() => Task.FromException<List<int>?>(new NullReferenceException())));
    }

    [TestMethod]
    public async Task SendAsyncMapsResponsesAndExceptions()
    {
        Assert.IsNull(await ApiResult.SendAsync(() => Task.FromResult(Response(HttpStatusCode.OK))));
        Assert.AreEqual("The server had a problem (HTTP 500). Try again.",
            await ApiResult.SendAsync(() => Task.FromResult(Response(HttpStatusCode.InternalServerError))));
        Assert.AreEqual(ApiResult.UnreachableMessage,
            await ApiResult.SendAsync(() => Task.FromException<HttpResponseMessage>(new HttpRequestException("Offline"))));
        Assert.AreEqual(ApiResult.UnreachableMessage,
            await ApiResult.SendAsync(() => Task.FromException<HttpResponseMessage>(new TaskCanceledException())));
    }

    [TestMethod]
    public async Task ServiceLoadAgainstAFailingServerYieldsTheServerMessage()
    {
        // A real service load through a stub handler: the non-success status reaches the page as a message.
        using var http = new HttpClient(new StatusHandler(HttpStatusCode.InternalServerError)) { BaseAddress = new Uri("https://example.test/") };
        var service = new GameService(http);
        var (games, error) = await ApiResult.LoadAsync(service.GetAllAsync);
        Assert.IsNull(games);
        Assert.AreEqual("The server had a problem (HTTP 500). Try again.", error);
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("boom") });
    }
}
