using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Components;

namespace MW_GC.EventManager.Web.Services;

/// <summary>
/// Sends the user to sign in when an API call shows that the session has expired, instead of
/// leaving the page with an error. Static Web Apps answers a signed-out /api/* call with a 401
/// that its response override turns into a redirect to the sign-in route. Depending on where that
/// redirect ends, the browser's fetch sees a 401, a followed redirect to /.auth/..., the sign-in
/// page as 200 text/html, or (when it ends on the identity provider's host) an opaque network failure.
/// Only requests whose path starts with /api/ are handled; everything else passes through.
/// The original response or exception is always returned, so callers behave as before.
/// </summary>
public sealed class SessionExpiredHandler(NavigationManager navigation) : DelegatingHandler
{
    private const string AuthPathPrefix = "/.auth/";

    // At most one sign-in navigation per page load. The handler lives as long as the app's HttpClient,
    // and the forced navigation reloads the app, which starts again with a new handler.
    private int _navigated;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!IsApiRequest(request)) return await base.SendAsync(request, cancellationToken);

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        // A cancellation or the HttpClient timeout is an OperationCanceledException, not this.
        catch (HttpRequestException) when (!cancellationToken.IsCancellationRequested && !HasNavigated)
        {
            // An outage looks the same as a cross-origin sign-in redirect, so ask SWA whether the
            // user is still signed in before leaving the page. Otherwise the unreachable message shows.
            if (await IsSignedOutAsync(cancellationToken)) NavigateToSignIn();
            throw;
        }

        if (IsSessionExpired(response)) NavigateToSignIn();
        return response;
    }

    private bool HasNavigated => Volatile.Read(ref _navigated) != 0;

    private static bool IsApiRequest(HttpRequestMessage request) =>
        request.RequestUri is { IsAbsoluteUri: true } uri && uri.AbsolutePath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);

    private static bool IsSessionExpired(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized) return true;
        // The browser followed the redirect to the sign-in route.
        if (response.RequestMessage?.RequestUri is { IsAbsoluteUri: true } final &&
            final.AbsolutePath.StartsWith(AuthPathPrefix, StringComparison.OrdinalIgnoreCase)) return true;
        // The sign-in page itself, as ApiResult treats it: the API never returns HTML.
        return response.IsSuccessStatusCode && response.Content?.Headers.ContentType?.MediaType == "text/html";
    }

    // True only when SWA's own /.auth/me (same origin as the page) answers and says nobody is signed in.
    private async Task<bool> IsSignedOutAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var probe = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(navigation.BaseUri), "/.auth/me"));
            using var response = await base.SendAsync(probe, cancellationToken);
            if (!response.IsSuccessStatusCode) return false;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("clientPrincipal", out var principal) &&
                principal.ValueKind == JsonValueKind.Null;
        }
        catch (Exception ex) when (ApiResult.IsApiFailure(ex))
        {
            return false;
        }
    }

    private void NavigateToSignIn()
    {
        if (Interlocked.Exchange(ref _navigated, 1) != 0) return;
        var returnPath = "/" + navigation.ToBaseRelativePath(navigation.Uri);
        navigation.NavigateTo(ApiResult.SignInUrl(returnPath), forceLoad: true);
    }
}
