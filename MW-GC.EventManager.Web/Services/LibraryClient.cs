using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
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

/// <summary>The four Library clients and the API route each one serves, registered once for the app (Program.cs).</summary>
public static class LibraryClients
{
    /// <summary>Registers a <see cref="LibraryClient{T}"/> per Library list over the app's <see cref="HttpClient"/>.</summary>
    public static IServiceCollection AddLibraryClients(this IServiceCollection services) => services
        .AddScoped(sp => new LibraryClient<GameEntity>(sp.GetRequiredService<HttpClient>(), "games"))
        .AddScoped(sp => new LibraryClient<ActivityEntity>(sp.GetRequiredService<HttpClient>(), "activities"))
        .AddScoped(sp => new LibraryClient<ThemeEntity>(sp.GetRequiredService<HttpClient>(), "themes"))
        .AddScoped(sp => new LibraryClient<HolidayEntity>(sp.GetRequiredService<HttpClient>(), "holidays"));
}
