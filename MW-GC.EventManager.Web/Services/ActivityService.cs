using System.Net.Http.Json;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Web.Services;

public sealed class ActivityService(HttpClient http)
{
    public Task<List<ActivityEntity>?> GetAllAsync() => http.GetFromJsonAsync<List<ActivityEntity>>("api/activities");
    public Task<ActivityEntity?> GetAsync(Guid id) => http.GetFromJsonAsync<ActivityEntity>($"api/activities/{id}");
    public Task<HttpResponseMessage> CreateAsync(ActivityEntity entity) => http.PostAsJsonAsync("api/activities", entity);
    public Task<HttpResponseMessage> UpdateAsync(ActivityEntity entity) => http.PutAsJsonAsync($"api/activities/{entity.Id}", entity);
    public Task<HttpResponseMessage> DeleteAsync(Guid id) => http.DeleteAsync($"api/activities/{id}");
    public Task<HttpResponseMessage> DuplicateAsync(Guid id) => http.PostAsync($"api/activities/{id}/duplicate", null);

    public Task<HttpResponseMessage> UpdateCommentsAsync(Guid id, string? comments) =>
        http.PatchAsJsonAsync($"api/activities/{id}/comments", new { Comments = comments });
}
