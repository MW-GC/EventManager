using System.Net.Http.Json;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Web.Services;

public sealed class HolidayService(HttpClient http)
{
    public Task<List<HolidayEntity>?> GetAllAsync() => http.GetFromJsonAsync<List<HolidayEntity>>("api/holidays");
    public Task<HttpResponseMessage> CreateAsync(HolidayEntity entity) => http.PostAsJsonAsync("api/holidays", entity);
    public Task<HttpResponseMessage> UpdateAsync(HolidayEntity entity) => http.PutAsJsonAsync($"api/holidays/{entity.Id}", entity);
    public Task<HttpResponseMessage> DeleteAsync(Guid id) => http.DeleteAsync($"api/holidays/{id}");
}
