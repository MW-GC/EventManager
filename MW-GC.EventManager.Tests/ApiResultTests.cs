using System.Net;
using System.Text;
using System.Text.Json;
using MW_GC.EventManager.Web.Services;
using Xunit;

namespace MW_GC.EventManager.Tests;

// The shared helper's message mapping for each status class, transport errors and timeouts.
public class ApiResultTests
{
    private static HttpResponseMessage Response(HttpStatusCode status, string? body = null, string mediaType = "text/plain") =>
        new(status) { Content = body is null ? new StringContent(string.Empty) : new StringContent(body, Encoding.UTF8, mediaType) };

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task SuccessHasNoMessage(HttpStatusCode status)
    {
        using var response = Response(status, "ignored");
        Assert.Null(await ApiResult.ErrorAsync(response));
    }

    [Fact]
    public async Task SuccessThatIsTheSignInPageIsNotASuccess()
    {
        // SWA redirects an expired session to its sign-in page; fetch follows and returns HTML with 200.
        using var response = Response(HttpStatusCode.OK, "<html><body>Sign in</body></html>", "text/html");
        Assert.Equal("Your session expired. Sign in again.", await ApiResult.ErrorAsync(response));
        using var json = Response(HttpStatusCode.OK, "{}", "application/json");
        Assert.Null(await ApiResult.ErrorAsync(json));
    }

    [Fact]
    public async Task UnauthorizedSaysTheSessionExpired()
    {
        using var response = Response(HttpStatusCode.Unauthorized, "Some server text");
        var message = await ApiResult.ErrorAsync(response);
        Assert.Equal("Your session expired. Sign in again.", message);
        Assert.True(ApiResult.IsSessionExpired(message));
    }

    [Fact]
    public void SignInUrlReturnsToTheCurrentPath()
    {
        Assert.Equal("/.auth/login/aad?post_login_redirect_uri=%2Fgames", ApiResult.SignInUrl("/games"));
        Assert.Equal("/.auth/login/aad?post_login_redirect_uri=%2F", ApiResult.SignInUrl(""));
        Assert.False(ApiResult.IsSessionExpired("Not found. It may have been deleted."));
        Assert.False(ApiResult.IsSessionExpired(null));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(HttpStatusCode.UnsupportedMediaType)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task RejectedRequestShowsTheApisShortPlainText(HttpStatusCode status)
    {
        using var response = Response(status, "  Select between 1 and 5 activities.  ");
        Assert.Equal("Select between 1 and 5 activities.", await ApiResult.ErrorAsync(response));
    }

    [Fact]
    public async Task RejectedRequestShowsAJsonStringBody()
    {
        // BadRequestObjectResult(string) is serialized as a JSON string by the Functions host.
        using var response = Response(HttpStatusCode.BadRequest, JsonSerializer.Serialize("Event has no selections."), "application/json");
        Assert.Equal("Event has no selections.", await ApiResult.ErrorAsync(response));
    }

    [Fact]
    public async Task RejectedRequestShowsAProblemDetailsTitle()
    {
        const string problem = "{\"type\":\"https://tools.ietf.org/html/rfc9110#section-15.5.1\",\"title\":\"One or more validation errors occurred.\",\"status\":400}";
        using var response = Response(HttpStatusCode.BadRequest, problem, "application/problem+json");
        Assert.Equal("One or more validation errors occurred.", await ApiResult.ErrorAsync(response));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, null)]
    [InlineData(HttpStatusCode.BadRequest, "   ")]
    [InlineData(HttpStatusCode.Conflict, "<html><body>Conflict</body></html>")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "{\"errors\":{\"Name\":[\"Required\"]}}")]
    [InlineData(HttpStatusCode.BadRequest, "{ not json")]
    [InlineData(HttpStatusCode.BadRequest, "[1,2]")]
    public async Task RejectedRequestWithoutAUsableBodyGetsTheGenericMessage(HttpStatusCode status, string? body)
    {
        using var response = Response(status, body);
        Assert.Equal($"The request was not accepted (HTTP {(int)status}).", await ApiResult.ErrorAsync(response));
    }

    [Fact]
    public async Task RejectedRequestWithALongBodyGetsTheGenericMessage()
    {
        using var response = Response(HttpStatusCode.BadRequest, new string('x', 301));
        Assert.Equal("The request was not accepted (HTTP 400).", await ApiResult.ErrorAsync(response));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData((HttpStatusCode)429)]
    public async Task OtherClientErrorsDoNotEchoTheBody(HttpStatusCode status)
    {
        using var response = Response(status, "internal detail");
        Assert.Equal($"The request was not accepted (HTTP {(int)status}).", await ApiResult.ErrorAsync(response));
    }

    [Fact]
    public async Task NotFoundSaysItMayHaveBeenDeleted()
    {
        using var response = Response(HttpStatusCode.NotFound, "Game 123 not found");
        Assert.Equal("Not found. It may have been deleted.", await ApiResult.ErrorAsync(response));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task ServerErrorNamesTheCodeAndNeverShowsTheBody(HttpStatusCode status)
    {
        using var response = Response(status, "System.InvalidOperationException: boom\n   at Stack.Trace()");
        var message = await ApiResult.ErrorAsync(response);
        Assert.Equal($"The server had a problem (HTTP {(int)status}). Try again.", message);
        Assert.DoesNotContain("Exception", message);
    }

    [Fact]
    public void TransportErrorAndTimeoutSayTheServerCouldNotBeReached()
    {
        const string expected = "Could not reach the server. Check the connection and try again.";
        Assert.Equal(expected, ApiResult.FromException(new HttpRequestException("No such host is known (api.example)")));
        Assert.Equal(expected, ApiResult.FromException(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing.")));
        Assert.Equal(expected, ApiResult.FromException(new OperationCanceledException()));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Your session expired. Sign in again.")]
    [InlineData(HttpStatusCode.NotFound, "Not found. It may have been deleted.")]
    [InlineData(HttpStatusCode.InternalServerError, "The server had a problem (HTTP 500). Try again.")]
    [InlineData(HttpStatusCode.BadRequest, "The request was not accepted (HTTP 400).")]
    public void LoadFailureWithAStatusMapsLikeAResponse(HttpStatusCode status, string expected)
    {
        // GetFromJsonAsync throws HttpRequestException with StatusCode set for a non-success response.
        Assert.Equal(expected, ApiResult.FromException(new HttpRequestException("Response status code does not indicate success", null, status)));
    }

    [Fact]
    public void UnreadableBodyNeverShowsTheExceptionText()
    {
        var message = ApiResult.FromException(new JsonException("'<' is an invalid start of a value. Path: $ | LineNumber: 0"));
        Assert.Equal(ApiResult.UnreadableMessage, message);
        Assert.DoesNotContain("LineNumber", message);
    }

    [Fact]
    public async Task LoadAsyncReturnsTheValueOrTheMessageAndNeverThrows()
    {
        var ok = await ApiResult.LoadAsync(() => Task.FromResult<List<int>?>([1, 2]));
        Assert.Equal([1, 2], ok.Value);
        Assert.Null(ok.Error);

        var failed = await ApiResult.LoadAsync<List<int>>(() => Task.FromException<List<int>?>(new HttpRequestException("Offline")));
        Assert.Null(failed.Value);
        Assert.Equal(ApiResult.UnreachableMessage, failed.Error);

        var timedOut = await ApiResult.LoadAsync<List<int>>(() => Task.FromException<List<int>?>(new TaskCanceledException()));
        Assert.Equal(ApiResult.UnreachableMessage, timedOut.Error);
    }

    [Fact]
    public async Task LoadAsyncDoesNotSwallowProgrammingErrors()
    {
        await Assert.ThrowsAsync<NullReferenceException>(() =>
            ApiResult.LoadAsync<List<int>>(() => Task.FromException<List<int>?>(new NullReferenceException())));
    }

    [Fact]
    public async Task SendAsyncMapsResponsesAndExceptions()
    {
        Assert.Null(await ApiResult.SendAsync(() => Task.FromResult(Response(HttpStatusCode.OK))));
        Assert.Equal("The server had a problem (HTTP 500). Try again.",
            await ApiResult.SendAsync(() => Task.FromResult(Response(HttpStatusCode.InternalServerError))));
        Assert.Equal(ApiResult.UnreachableMessage,
            await ApiResult.SendAsync(() => Task.FromException<HttpResponseMessage>(new HttpRequestException("Offline"))));
        Assert.Equal(ApiResult.UnreachableMessage,
            await ApiResult.SendAsync(() => Task.FromException<HttpResponseMessage>(new TaskCanceledException())));
    }

    [Fact]
    public async Task ServiceLoadAgainstAFailingServerYieldsTheServerMessage()
    {
        // A real service load through a stub handler: the non-success status reaches the page as a message.
        using var http = new HttpClient(new StatusHandler(HttpStatusCode.InternalServerError)) { BaseAddress = new Uri("https://example.test/") };
        var service = new GameService(http);
        var (games, error) = await ApiResult.LoadAsync(service.GetAllAsync);
        Assert.Null(games);
        Assert.Equal("The server had a problem (HTTP 500). Try again.", error);
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("boom") });
    }
}
