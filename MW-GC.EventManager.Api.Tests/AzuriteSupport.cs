namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// The two environment variables of the tests that run against real Table Storage (Azurite).
/// Both are optional, so <c>dotnet test</c> on the solution needs no emulator: without
/// <see cref="ConnectionVariable"/> those tests are reported as skipped (inconclusive).
/// </summary>
internal static class AzuriteSupport
{
    /// <summary>The Azurite connection string; when unset (or blank) the Azurite tests skip.</summary>
    public const string ConnectionVariable = "EM_AZURITE_CONNECTION";

    /// <summary>
    /// The base address of a running API on the same Azurite, ending in <c>/api</c>; when unset (or
    /// blank) the checks through it are left out.
    /// </summary>
    public const string ApiUrlVariable = "EM_API_URL";

    /// <summary>The Azurite connection string, or marks the calling test inconclusive (skipped).</summary>
    public static string Connection()
    {
        var connection = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(connection))
            Assert.Inconclusive($"Azurite test skipped: set {ConnectionVariable} to an Azurite connection string.");
        return connection;
    }

    /// <summary>The running API's base address from <see cref="ApiUrlVariable"/>, or null when it is not set.</summary>
    public static string? ApiUrl() => ApiUrlFrom(Environment.GetEnvironmentVariable(ApiUrlVariable));

    /// <summary>A blank value counts as unset; otherwise the value trimmed, without a trailing slash.</summary>
    public static string? ApiUrlFrom(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd('/');
}
