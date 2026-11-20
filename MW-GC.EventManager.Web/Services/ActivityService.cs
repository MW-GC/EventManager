using System.Net.Http.Json;

namespace MW_GC.EventManager.Web.Services;

/// <summary>
/// The Activity routes no other Library list has. Read, create, update and delete go through
/// <see cref="LibraryClient{T}"/> with the route <c>activities</c>.
/// </summary>
public sealed class ActivityService(HttpClient http)
{
    public Task<HttpResponseMessage> DuplicateAsync(Guid id) => http.PostAsync($"api/activities/{id}/duplicate", null);

    public Task<HttpResponseMessage> UpdateCommentsAsync(Guid id, string? comments) =>
        http.PatchAsJsonAsync($"api/activities/{id}/comments", new { Comments = comments });
}
