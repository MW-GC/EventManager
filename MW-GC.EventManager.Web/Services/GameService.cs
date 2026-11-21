using System.Net.Http.Json;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Web.Services;

// The Games list for the readers not yet moved onto LibraryClient<GameEntity>: the Events page, the Stats page
// and the Activity details dialog's performance panel. The Games and Activities pages read it through
// LibraryClient<GameEntity>, which sends the same request.
public sealed class GameService(HttpClient http)
{
    public Task<List<GameEntity>?> GetAllAsync() => http.GetFromJsonAsync<List<GameEntity>>("api/games");
}
