using System.Net.Http.Json;
using Azure;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;

namespace MW_GC.EventManager.Web.Services;

public sealed class EventService(HttpClient http)
{
    public Task<List<EventEntity>?> GetAllAsync() => http.GetFromJsonAsync<List<EventEntity>>("api/events");
    public Task<EventEntity?> GetAsync(Guid id) => http.GetFromJsonAsync<EventEntity>($"api/events/{id}");
    public async Task<HttpResponseMessage> SaveAsync(EventEntity entity, Guid idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/events")
        {
            Content = JsonContent.Create(entity)
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey.ToString("D"));
        return await http.SendAsync(request);
    }

    /// <summary>
    /// Replaces the Event. <paramref name="eTag"/> is the Event's tag as the page read it (#204 story 22), sent as
    /// <c>If-Match</c>, so an Event another admin saved meanwhile is the API's 409; none (default) sends no header.
    /// </summary>
    public async Task<HttpResponseMessage> UpdateAsync(EventEntity entity, ETag eTag = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/events/{entity.Id}")
        {
            Content = JsonContent.Create(entity)
        };
        IfMatch(request, eTag);
        return await http.SendAsync(request);
    }

    /// <summary>Deletes the Event (cancels one Occurrence), under <paramref name="eTag"/> as <see cref="UpdateAsync"/> does.</summary>
    public async Task<HttpResponseMessage> DeleteAsync(Guid id, ETag eTag = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"api/events/{id}");
        IfMatch(request, eTag);
        return await http.SendAsync(request);
    }

    // #101 (admin only): the Event's anonymous feedback token, created on first request, and its like count.
    public Task<FeedbackLink?> GetFeedbackLinkAsync(Guid id) => http.GetFromJsonAsync<FeedbackLink>($"api/events/{id}/feedback-link");
    public Task<EventLikes?> GetLikesAsync(Guid id) => http.GetFromJsonAsync<EventLikes>($"api/events/{id}/likes");

    // Azure's weak tags (W/"datetime'...'") are sent as read, without .NET's header parsing.
    private static void IfMatch(HttpRequestMessage request, ETag eTag)
    {
        if (eTag != default) request.Headers.TryAddWithoutValidation("If-Match", eTag.ToString());
    }
}
