using System.Net.Http.Json;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Web.Services;

public sealed class GameService(HttpClient http)
{
    public Task<List<GameEntity>?> GetAllAsync() => http.GetFromJsonAsync<List<GameEntity>>("api/games");
    public Task<GameEntity?> GetAsync(Guid id) => http.GetFromJsonAsync<GameEntity>($"api/games/{id}");
    public Task<HttpResponseMessage> CreateAsync(GameEntity entity) => http.PostAsJsonAsync("api/games", entity);
    public Task<HttpResponseMessage> UpdateAsync(GameEntity entity) => http.PutAsJsonAsync($"api/games/{entity.Id}", entity);
    public Task<HttpResponseMessage> DeleteAsync(Guid id) => http.DeleteAsync($"api/games/{id}");
}
