using System.Net.Http.Json;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Web.Services;

/// <summary>
/// One Library list on the API (Games, Themes, Holidays or Activities): <c>api/{route}</c> and
/// <c>api/{route}/{id}</c>, with the same verbs, headers and bodies as the per-list services. A load
/// throws on failure, as those services do, so a page reads it through <see cref="LoadErrors"/>; a write
/// returns null on success or the <see cref="ApiResult"/> message for the failure, and never throws for one.
/// </summary>
public sealed class LibraryClient<T>(HttpClient http, string route) where T : EntityBase
{
    public Task<List<T>?> GetAllAsync() => http.GetFromJsonAsync<List<T>>($"api/{route}");

    public Task<string?> CreateAsync(T item) => ApiResult.SendAsync(() => http.PostAsJsonAsync($"api/{route}", item));

    public Task<string?> UpdateAsync(T item) => ApiResult.SendAsync(() => http.PutAsJsonAsync($"api/{route}/{item.Id}", item));

    public Task<string?> DeleteAsync(Guid id) => ApiResult.SendAsync(() => http.DeleteAsync($"api/{route}/{id}"));
}
