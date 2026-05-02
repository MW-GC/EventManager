using System.Net.Http.Json;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Web.Services;

public sealed class ThemeService(HttpClient http)
{
    public Task<List<ThemeEntity>?> GetAllAsync() => http.GetFromJsonAsync<List<ThemeEntity>>("api/themes");
    public Task<HttpResponseMessage> CreateAsync(ThemeEntity entity) => http.PostAsJsonAsync("api/themes", entity);
    public Task<HttpResponseMessage> UpdateAsync(ThemeEntity entity) => http.PutAsJsonAsync($"api/themes/{entity.Id}", entity);
    public Task<HttpResponseMessage> DeleteAsync(Guid id) => http.DeleteAsync($"api/themes/{id}");
}
