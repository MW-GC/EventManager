using Microsoft.Extensions.DependencyInjection;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Web.Services;

/// <summary>The four Library clients and the API route each one serves, registered once for the app (<see cref="AppServices"/>).</summary>
public static class LibraryClients
{
    /// <summary>Registers a <see cref="LibraryClient{T}"/> per Library list over the app's <see cref="HttpClient"/>.</summary>
    public static IServiceCollection AddLibraryClients(this IServiceCollection services) => services
        .AddScoped(sp => new LibraryClient<GameEntity>(sp.GetRequiredService<HttpClient>(), "games"))
        .AddScoped(sp => new LibraryClient<ActivityEntity>(sp.GetRequiredService<HttpClient>(), "activities"))
        .AddScoped(sp => new LibraryClient<ThemeEntity>(sp.GetRequiredService<HttpClient>(), "themes"))
        .AddScoped(sp => new LibraryClient<HolidayEntity>(sp.GetRequiredService<HttpClient>(), "holidays"));
}
