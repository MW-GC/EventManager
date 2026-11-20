namespace MW_GC.EventManager.E2ETests;

/// <summary>
/// Where the browser tests find the running app. They drive a real stack (Azurite, the Functions API and the
/// Static Web Apps CLI emulator in front of the Web build), so they only run when <c>EM_E2E_URL</c> names the
/// emulator's address. Without it every test is inconclusive, and <c>dotnet test</c> on the solution needs
/// neither browsers nor a stack. The E2E workflow also sets <c>EM_E2E_REQUIRED=1</c>, which turns a missing
/// address into a failure, so the check cannot pass with no flow executed.
/// </summary>
internal static class E2EEnvironment
{
    public const string UrlVariable = "EM_E2E_URL";

    public const string RequiredVariable = "EM_E2E_REQUIRED";

    /// <summary>
    /// The app's base URL without a trailing slash. When it is not set the test is inconclusive, or fails when
    /// <c>EM_E2E_REQUIRED</c> is <c>1</c>.
    /// </summary>
    public static string AppUrlOrSkip()
    {
        var url = Environment.GetEnvironmentVariable(UrlVariable);
        if (string.IsNullOrWhiteSpace(url))
        {
            if (Environment.GetEnvironmentVariable(RequiredVariable)?.Trim() == "1")
            {
                Assert.Fail(
                    $"Browser test required ({RequiredVariable}=1) but {UrlVariable} is not set: set it to the " +
                    "Static Web Apps emulator address of the running stack. See \"Browser tests (E2E)\" in the README.");
            }

            Assert.Inconclusive(
                $"Browser test skipped: set {UrlVariable} to the Static Web Apps emulator address (for example " +
                "http://localhost:4280) of a running stack. See \"Browser tests (E2E)\" in the README.");
        }

        return url!.Trim().TrimEnd('/');
    }
}
