namespace MW_GC.EventManager.E2ETests;

/// <summary>
/// Where the browser tests find the running app. They drive a real stack (Azurite, the Functions API and the
/// Static Web Apps CLI emulator in front of the Web build), so they only run when <c>EM_E2E_URL</c> names the
/// emulator's address. Without it every test is inconclusive, and <c>dotnet test</c> on the solution needs
/// neither browsers nor a stack.
/// </summary>
internal static class E2EEnvironment
{
    public const string UrlVariable = "EM_E2E_URL";

    /// <summary>The app's base URL without a trailing slash, or an inconclusive test when it is not set.</summary>
    public static string AppUrlOrSkip()
    {
        var url = Environment.GetEnvironmentVariable(UrlVariable);
        if (string.IsNullOrWhiteSpace(url))
        {
            Assert.Inconclusive(
                $"Browser test skipped: set {UrlVariable} to the Static Web Apps emulator address (for example " +
                "http://localhost:4280) of a running stack. See \"Browser tests (E2E)\" in the README.");
        }

        return url!.Trim().TrimEnd('/');
    }
}
