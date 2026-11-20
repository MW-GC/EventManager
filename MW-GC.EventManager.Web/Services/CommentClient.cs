using System.Net;
using System.Net.Http.Json;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Web.Services;

/// <summary>
/// An Activity's Comments on the API (#131, ADR 0001): <c>GET</c> and <c>POST api/activities/{id}/comments</c>,
/// <c>PUT</c> and <c>DELETE api/activities/{id}/comments/{commentId}</c>. Each Comment is its own row with its
/// own eTag (<see cref="EntityBase.ETag"/>, read from the JSON body), which an edit or delete sends back as
/// If-Match, so two Comments never conflict with each other. Never throws for an API failure: the result
/// carries the message, or <see cref="CommentResult.Conflict"/> for a 409 and
/// <see cref="CommentResult.NotFound"/> for a 404.
/// </summary>
public sealed class CommentClient(HttpClient http)
{
    /// <summary>The Activity's Comments in the API's order: oldest first.</summary>
    public async Task<CommentListResult> GetAllAsync(Guid activityId)
    {
        try
        {
            using var response = await http.GetAsync($"api/activities/{activityId}/comments");
            if (await ApiResult.ErrorAsync(response) is { } error) return new(null, error);
            return await response.Content.ReadFromJsonAsync<List<CommentEntity>>() is { } comments
                ? new(comments, null)
                : new(null, ApiResult.UnreadableMessage);
        }
        catch (Exception ex) when (ApiResult.IsApiFailure(ex))
        {
            return new(null, ApiResult.FromException(ex));
        }
    }

    /// <summary>Adds a Comment with only its text; the API sets its id and added date.</summary>
    public Task<CommentResult> AddAsync(Guid activityId, string text) =>
        SendAsync(HttpMethod.Post, $"api/activities/{activityId}/comments", text, null);

    /// <summary>Changes only the text. <paramref name="eTag"/> is that Comment's tag as last read.</summary>
    public Task<CommentResult> UpdateAsync(Guid activityId, Guid commentId, string text, string? eTag) =>
        SendAsync(HttpMethod.Put, $"api/activities/{activityId}/comments/{commentId}", text, eTag);

    /// <summary>Deletes one Comment. <paramref name="eTag"/> is that Comment's tag as last read.</summary>
    public Task<CommentResult> DeleteAsync(Guid activityId, Guid commentId, string? eTag) =>
        SendAsync(HttpMethod.Delete, $"api/activities/{activityId}/comments/{commentId}", null, eTag);

    private async Task<CommentResult> SendAsync(HttpMethod method, string path, string? text, string? eTag)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (text is not null) request.Content = JsonContent.Create(new { text });
            // Azure's weak tags (W/"datetime'...'") are sent as read, without .NET's header parsing.
            if (eTag is not null) request.Headers.TryAddWithoutValidation("If-Match", eTag);
            using var response = await http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.Conflict) return CommentResult.Stale;
            if (await ApiResult.ErrorAsync(response) is { } error)
                return new(null, error, false, response.StatusCode == HttpStatusCode.NotFound);
            if (method == HttpMethod.Delete) return CommentResult.Done;
            return await response.Content.ReadFromJsonAsync<CommentEntity>() is { } comment
                ? new(comment, null, false, false)
                : CommentResult.Failed(ApiResult.UnreadableMessage);
        }
        catch (Exception ex) when (ApiResult.IsApiFailure(ex))
        {
            return CommentResult.Failed(ApiResult.FromException(ex));
        }
    }
}

/// <summary>A read of an Activity's Comments: the list, or the failure message.</summary>
public sealed record CommentListResult(List<CommentEntity>? Comments, string? Error);

/// <summary>
/// An add, edit or delete of one Comment: the stored Comment (none for a delete), or the failure message,
/// or <see cref="Conflict"/> when someone else changed it first (409), or <see cref="NotFound"/> (404, with
/// the message too).
/// </summary>
public sealed record CommentResult(CommentEntity? Comment, string? Error, bool Conflict, bool NotFound)
{
    public static CommentResult Failed(string error) => new(null, error, false, false);
    public static readonly CommentResult Stale = new(null, null, true, false);
    public static readonly CommentResult Done = new(null, null, false, false);
}
