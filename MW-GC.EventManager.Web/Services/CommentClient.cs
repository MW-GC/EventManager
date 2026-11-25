using System.Net;
using System.Net.Http.Json;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Web.Services;

/// <summary>
/// An Activity's Comments on the API (#131, ADR 0001): <c>GET</c> and <c>POST api/activities/{id}/comments</c>,
/// <c>PUT</c> and <c>DELETE api/activities/{id}/comments/{commentId}</c>. Each Comment is its own row with its
/// own eTag (<see cref="EntityBase.ETag"/>, read from the JSON body), which an edit or delete sends back as
/// If-Match, so two Comments never conflict with each other. Never throws for an API failure: a write returns
/// a <see cref="CommentResult"/> that says what happened.
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

    /// <summary>
    /// Adds a Comment; the API sets its id and added date. <paramref name="eventId"/> is the Event it was
    /// written from (#132), sent only when given; the API refuses an Event that does not exist.
    /// </summary>
    public Task<CommentResult> AddAsync(Guid activityId, string text, Guid? eventId = null) =>
        SendAsync(HttpMethod.Post, $"api/activities/{activityId}/comments", TextBody(text, eventId), null);

    /// <summary>Changes only the text. <paramref name="eTag"/> is that Comment's tag as last read.</summary>
    public Task<CommentResult> UpdateAsync(Guid activityId, Guid commentId, string text, string? eTag) =>
        SendAsync(HttpMethod.Put, $"api/activities/{activityId}/comments/{commentId}", TextBody(text), eTag);

    /// <summary>Deletes one Comment. <paramref name="eTag"/> is that Comment's tag as last read.</summary>
    public Task<CommentResult> DeleteAsync(Guid activityId, Guid commentId, string? eTag) =>
        SendAsync(HttpMethod.Delete, $"api/activities/{activityId}/comments/{commentId}", null, eTag);

    // The body of an add or edit: the text, and the Event only when one is given.
    private static JsonContent TextBody(string text, Guid? eventId = null) =>
        eventId is { } id ? JsonContent.Create(new { text, eventId = id }) : JsonContent.Create(new { text });

    // The try/catch, If-Match and 409 handling repeat EventMetricsClient.SaveAsync on purpose: that file belongs to
    // another change, so a helper both can share is left for a later one.
    private async Task<CommentResult> SendAsync(HttpMethod method, string path, HttpContent? body, string? eTag)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            request.Content = body;
            // Azure's weak tags (W/"datetime'...'") are sent as read, without .NET's header parsing.
            if (eTag is not null) request.Headers.TryAddWithoutValidation("If-Match", eTag);
            using var response = await http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.Conflict) return CommentResult.Stale.Instance;
            if (response.StatusCode == HttpStatusCode.NotFound) return CommentResult.NotFound.Instance;
            if (await ApiResult.ErrorAsync(response) is { } error) return new CommentResult.Failed(error);
            if (method == HttpMethod.Delete) return CommentResult.Deleted.Instance;
            return await response.Content.ReadFromJsonAsync<CommentEntity>() is { } comment
                ? new CommentResult.Stored(comment)
                : new CommentResult.Failed(ApiResult.UnreadableMessage);
        }
        catch (Exception ex) when (ApiResult.IsApiFailure(ex))
        {
            return new CommentResult.Failed(ApiResult.FromException(ex));
        }
    }
}

/// <summary>A read of an Activity's Comments: the list, or the failure message.</summary>
public sealed record CommentListResult(List<CommentEntity>? Comments, string? Error);

/// <summary>An add, edit or delete of one Comment: exactly one of the five outcomes below.</summary>
public abstract record CommentResult
{
    private CommentResult() { }

    /// <summary>The API stored an add or edit: the Comment as stored, with its new tag.</summary>
    public sealed record Stored(CommentEntity Comment) : CommentResult;

    /// <summary>The API answered a delete with success (204), which it also does for a Comment already gone.</summary>
    public sealed record Deleted : CommentResult
    {
        public static readonly Deleted Instance = new();
    }

    /// <summary>The request failed; the message says why.</summary>
    public sealed record Failed(string Error) : CommentResult;

    /// <summary>Someone else changed the Comment first (409): the tag sent was stale.</summary>
    public sealed record Stale : CommentResult
    {
        public static readonly Stale Instance = new();
    }

    /// <summary>
    /// A 404 on any write; <see cref="ApiResult.NotFoundMessage"/> says so. The API sends it for an edit of a Comment
    /// or an add to an Activity that no longer exists. A delete never gets it for a Comment that is already gone (the
    /// API answers that with 204), but a 404 on a delete (from anything between the page and the API) is still this
    /// outcome, never a success, so a delete that did not reach the API never removes the row.
    /// </summary>
    public sealed record NotFound : CommentResult
    {
        public static readonly NotFound Instance = new();
    }
}
