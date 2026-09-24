using System.Net.Http.Json;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Requests;

namespace MW_GC.EventManager.Web.Services;

public sealed class EventService(HttpClient http)
{
    public Task<List<EventEntity>?> GetAllAsync() => http.GetFromJsonAsync<List<EventEntity>>("api/events");
    public Task<EventEntity?> GetAsync(Guid id) => http.GetFromJsonAsync<EventEntity>($"api/events/{id}");
    public Task<HttpResponseMessage> GenerateAsync(GenerateEventRequest request) => http.PostAsJsonAsync("api/events/generate", request);
    public async Task<HttpResponseMessage> SaveAsync(EventEntity entity, Guid idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/events")
        {
            Content = JsonContent.Create(entity)
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey.ToString("D"));
        return await http.SendAsync(request);
    }
    public Task<HttpResponseMessage> UpdateAsync(EventEntity entity) => http.PutAsJsonAsync($"api/events/{entity.Id}", entity);
    public Task<HttpResponseMessage> DeleteAsync(Guid id) => http.DeleteAsync($"api/events/{id}");
    public Task<HttpResponseMessage> SelectWinnerAsync(Guid id) => http.PostAsync($"api/events/{id}/winner", null);
}
