using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MW_GC.EventManager.Web.Services;

/// <summary>
/// Every service the app registers (Program.cs calls this and nothing else), in one place a test can build:
/// the API's HttpClient, Fluent UI, the four Library clients and the other API clients.
/// </summary>
public static class AppServices
{
    public static IServiceCollection AddAppServices(this IServiceCollection services, string apiBase)
    {
        // A stalled API call ends with a message after 30 seconds instead of a spinner that never stops.
        // An API call that finds the session expired sends the user to sign in (SessionExpiredHandler).
        services.AddScoped(sp => new HttpClient(
            new SessionExpiredHandler(sp.GetRequiredService<NavigationManager>()) { InnerHandler = new HttpClientHandler() })
        {
            BaseAddress = new Uri(apiBase),
            Timeout = TimeSpan.FromSeconds(30)
        });

        services.AddFluentUIComponents();

        // Games, Activities, Themes and Holidays: one LibraryClient each (ServiceRouteContractTests checks the routes).
        services.AddLibraryClients();
        services.AddScoped<ActivityService>();
        services.AddScoped<EventService>();
        services.AddScoped<SeriesService>();
        services.AddScoped<EventMetricsClient>();
        services.AddScoped<CommentClient>();
        services.AddScoped<FocusReturn>();
        return services;
    }
}
