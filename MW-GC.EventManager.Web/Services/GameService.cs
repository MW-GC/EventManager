using System.Net.Http.Json;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Web.Services;

// The Games list for the pages that read it (Activities, Events, Stats). The Games page reads and writes
// through LibraryClient<GameEntity>.
public sealed class GameService(HttpClient http)
{
    public Task<List<GameEntity>?> GetAllAsync() => http.GetFromJsonAsync<List<GameEntity>>("api/games");
}
