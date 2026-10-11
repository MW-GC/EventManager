using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace MW_GC.EventManager.E2ETests;

/// <summary>
/// Signs a browser context in to the Static Web Apps CLI emulator as an admin without its mock login form.
/// The emulator reads the client principal from the <c>StaticWebAppsAuthCookie</c> cookie (base64 JSON), so
/// the test sets that cookie directly. This is emulated authentication only; production uses Entra ID.
/// </summary>
internal static class EmulatorSignIn
{
    public const string CookieName = "StaticWebAppsAuthCookie";

    public static async Task AsAdminAsync(IBrowserContext context, string appUrl)
    {
        var principal = new
        {
            userId = "e2e-admin",
            userRoles = new[] { "anonymous", "authenticated", "admin" },
            identityProvider = "github",
            userDetails = "e2e-admin",
            claims = Array.Empty<object>(),
        };
        var value = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(principal)));

        await context.AddCookiesAsync([new Cookie { Name = CookieName, Value = value, Url = appUrl }]);
    }
}
