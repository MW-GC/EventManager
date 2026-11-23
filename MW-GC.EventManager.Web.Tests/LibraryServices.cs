using Microsoft.Extensions.DependencyInjection;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Tests;

// The Library clients as the app registers them (AddLibraryClients), over a test's own HttpClient: the one setup
// LibraryClientTests and ServiceRouteContractTests share (#196.8). The caller disposes the provider.
internal static class LibraryServices
{
    public static ServiceProvider Over(HttpClient http) =>
        new ServiceCollection().AddSingleton(http).AddLibraryClients().BuildServiceProvider();

    // Resolves the registered client for T and runs one call on it, disposing the provider afterwards.
    public static async Task CallAsync<T, TResult>(HttpClient http, Func<LibraryClient<T>, Task<TResult>> call) where T : EntityBase
    {
        await using var services = Over(http);
        await call(services.GetRequiredService<LibraryClient<T>>());
    }
}
