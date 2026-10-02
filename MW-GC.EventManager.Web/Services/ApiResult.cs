using System.Net;
using System.Text.Json;

namespace MW_GC.EventManager.Web.Services;

/// <summary>
/// Turns failed API calls into short messages a user can act on. Pages call it around every
/// service call so a failure shows a message instead of a spinner or a false success.
/// Exception text and stack traces are never shown.
/// </summary>
public static class ApiResult
{
    public const string SessionExpiredMessage = "Your session expired. Sign in again.";
    public const string NotFoundMessage = "Not found. It may have been deleted.";
    public const string UnreachableMessage = "Could not reach the server. Check the connection and try again.";
    public const string UnreadableMessage = "The server sent a response this page could not read. Try again.";

    // Response bodies longer than this are not shown; the generic message is used instead.
    private const int MaximumBodyLength = 300;

    /// <summary>The SWA sign-in route (staticwebapp.config.json signs in through AAD), returning to <paramref name="returnPath"/>.</summary>
    public static string SignInUrl(string returnPath) =>
        $"/.auth/login/aad?post_login_redirect_uri={Uri.EscapeDataString(string.IsNullOrEmpty(returnPath) ? "/" : returnPath)}";

    /// <summary>True when <paramref name="message"/> carries the session-expired message, so a sign-in link belongs next to it.</summary>
    public static bool IsSessionExpired(string? message) =>
        message is not null && message.Contains(SessionExpiredMessage, StringComparison.Ordinal);

    /// <summary>The Save Comments failure, worded the same on the Activities page and in Event details.</summary>
    public static string CommentsNotSaved(string error) => $"The comments were not saved. {error} Your text is kept.";

    /// <summary>
    /// Names the lists that failed to load, followed by the first failure's message, for example
    /// "Themes could not be loaded. Could not reach the server. ..." Null when nothing failed.
    /// </summary>
    public static string? ListsFailed(IEnumerable<(string Name, string? Error)> lists)
    {
        var failed = lists.Where(l => l.Error is not null).ToList();
        if (failed.Count == 0) return null;
        var names = failed.Select(f => f.Name).ToList();
        var named = names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
        return $"{named} could not be loaded. {failed[0].Error}";
    }

    /// <summary>Null for a success status; otherwise the message for the response.</summary>
    public static async Task<string?> ErrorAsync(HttpResponseMessage response)
    {
        // SWA answers an expired session with a redirect to its sign-in page, and the browser's fetch
        // follows it and returns that HTML page with 200. The API never returns HTML, so this is not a success.
        if (response.IsSuccessStatusCode)
            return response.Content?.Headers.ContentType?.MediaType == "text/html" ? SessionExpiredMessage : null;
        var status = response.StatusCode;
        if (ShowsBody(status) && await ReadShortMessageAsync(response) is { } body) return body;
        return ForStatus(status);
    }

    /// <summary>The message for a status code when no usable response body is available.</summary>
    public static string ForStatus(HttpStatusCode status)
    {
        var code = (int)status;
        return status switch
        {
            HttpStatusCode.Unauthorized => SessionExpiredMessage,
            HttpStatusCode.NotFound => NotFoundMessage,
            _ when code >= 500 => $"The server had a problem (HTTP {code}). Try again.",
            _ => $"The request was not accepted (HTTP {code})."
        };
    }

    /// <summary>The failures an API call can raise: transport, timeout, a failed status from a load, or an unreadable body.</summary>
    public static bool IsApiFailure(Exception ex) =>
        ex is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException;

    /// <summary>The message for an exception that <see cref="IsApiFailure"/> accepts.</summary>
    public static string FromException(Exception ex) => ex switch
    {
        // GetFromJsonAsync throws with the status code set for a non-success response.
        HttpRequestException { StatusCode: { } status } => ForStatus(status),
        HttpRequestException or OperationCanceledException => UnreachableMessage,
        _ => UnreadableMessage
    };

    /// <summary>Sends a write and returns null on success or the message on failure. Disposes the response.</summary>
    public static async Task<string?> SendAsync(Func<Task<HttpResponseMessage>> send)
    {
        try
        {
            using var response = await send();
            return await ErrorAsync(response);
        }
        catch (Exception ex) when (IsApiFailure(ex))
        {
            return FromException(ex);
        }
    }

    /// <summary>Runs a load and returns its value, or the message when it failed. Never throws for an API failure.</summary>
    public static async Task<(T? Value, string? Error)> LoadAsync<T>(Func<Task<T?>> load)
    {
        try
        {
            return (await load(), null);
        }
        catch (Exception ex) when (IsApiFailure(ex))
        {
            return (default, FromException(ex));
        }
    }

    private static bool ShowsBody(HttpStatusCode status) => status is
        HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.RequestEntityTooLarge or
        HttpStatusCode.UnsupportedMediaType or HttpStatusCode.UnprocessableEntity;

    // The API's own text when it is short plain text, a JSON string or a ProblemDetails title.
    private static async Task<string?> ReadShortMessageAsync(HttpResponseMessage response)
    {
        string body;
        try
        {
            body = (await response.Content.ReadAsStringAsync()).Trim();
        }
        catch (Exception ex) when (IsApiFailure(ex) || ex is InvalidOperationException)
        {
            return null;
        }
        if (body.Length == 0) return null;

        if (body[0] is '{' or '"')
        {
            try
            {
                using var json = JsonDocument.Parse(body);
                var root = json.RootElement;
                body = root.ValueKind switch
                {
                    JsonValueKind.String => root.GetString() ?? string.Empty,
                    JsonValueKind.Object when root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String
                        => title.GetString() ?? string.Empty,
                    _ => string.Empty
                };
                body = body.Trim();
            }
            catch (JsonException)
            {
                return null;
            }
        }
        else if (body[0] is '<' or '[')
        {
            return null;
        }

        return body.Length is > 0 and <= MaximumBodyLength ? body : null;
    }
}
