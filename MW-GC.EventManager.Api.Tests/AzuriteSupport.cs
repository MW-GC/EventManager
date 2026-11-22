using System.Net;
using Azure.Data.Tables;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// The two environment variables of the tests that run against real Table Storage (Azurite), and the
/// guard that keeps them on local Azurite. Both variables are optional, so <c>dotnet test</c> on the
/// solution needs no emulator: without <see cref="ConnectionVariable"/> those tests are reported as
/// skipped (inconclusive). They write, delete and convert rows, so they also skip, before any client
/// is built, when the connection string does not point at local Azurite (<see cref="IsLocalAzurite"/>).
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

    /// <summary>
    /// The Azurite connection string, or marks the calling test inconclusive (skipped) when it is unset
    /// or does not point at local Azurite. Nothing is contacted before that check.
    /// </summary>
    public static string Connection()
    {
        var connection = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(connection))
            Assert.Inconclusive($"Azurite test skipped: set {ConnectionVariable} to an Azurite connection string.");
        if (!IsLocalAzurite(connection))
            Assert.Inconclusive(NotLocalMessage);
        return connection;
    }

    /// <summary>Why an Azurite test skipped a connection string that is set but not local Azurite.</summary>
    public const string NotLocalMessage =
        $"Azurite test skipped: {ConnectionVariable} does not point at local Azurite (UseDevelopmentStorage=true, " +
        "an endpoint on 127.0.0.1 or localhost, or the devstoreaccount1 account). These tests write, delete and " +
        "convert rows, so they never run against other storage.";

    /// <summary>
    /// Whether a connection string points at local Azurite: <c>UseDevelopmentStorage=true</c>; or every
    /// endpoint it names is on a loopback host (<c>127.0.0.1</c>, <c>localhost</c>, <c>::1</c>); or it
    /// names Azurite's well-known account <c>devstoreaccount1</c> together with its own endpoints (an
    /// Azurite container reached by name). Account settings with no endpoint at all address Azure's
    /// public cloud, so they are never local, whatever the account name. A string that cannot be read is
    /// not local. Reads the string only; nothing is contacted.
    /// </summary>
    public static bool IsLocalAzurite(string? connection)
    {
        if (string.IsNullOrWhiteSpace(connection)) return false;
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in connection.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = part.IndexOf('=');
            if (at <= 0) return false;
            settings[part[..at].Trim()] = part[(at + 1)..].Trim();
        }

        if (settings.TryGetValue("UseDevelopmentStorage", out var development))
            return development.Equals("true", StringComparison.OrdinalIgnoreCase);

        var endpoints = settings.Where(s => s.Key.EndsWith("Endpoint", StringComparison.OrdinalIgnoreCase)).Select(s => s.Value).ToList();
        if (endpoints.Count == 0) return false;
        var hosts = new List<string>();
        foreach (var endpoint in endpoints)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return false;
            hosts.Add(uri.Host);
        }
        if (hosts.All(IsLoopback)) return true;
        return settings.TryGetValue("AccountName", out var account) && account == "devstoreaccount1";
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));

    /// <summary>The running API's base address from <see cref="ApiUrlVariable"/>, or null when it is not set.</summary>
    public static string? ApiUrl() => ApiUrlFrom(Environment.GetEnvironmentVariable(ApiUrlVariable));

    /// <summary>A blank value counts as unset; otherwise the value trimmed, without a trailing slash.</summary>
    public static string? ApiUrlFrom(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd('/');

    /// <summary>
    /// One flow line of an Azurite test, with what was expected and what storage gave, written to the
    /// test output; then the assertion.
    /// </summary>
    public static void Flow<T>(this TestContext context, string name, T expected, T actual)
    {
        var pass = EqualityComparer<T>.Default.Equals(expected, actual);
        context.WriteLine($"{(pass ? "PASS" : "FAIL")} {name}: expected {expected}, actual {actual}");
        Assert.AreEqual(expected, actual, name);
    }

    /// <summary>The old-text conversion's marker row as the test found it, to put back when it is done.</summary>
    public static async Task<TableEntity?> ReadMarkerAsync(TableClient comments, CancellationToken ct)
    {
        var found = await comments.GetEntityIfExistsAsync<TableEntity>(
            API.Services.ActivityNoteConversion.MarkerPartitionKey, API.Services.ActivityNoteConversion.MarkerRowKey, cancellationToken: ct);
        return found.HasValue ? found.Value : null;
    }

    /// <summary>
    /// Puts the marker back as <see cref="ReadMarkerAsync"/> found it: the same row (its columns) when there
    /// was one, none when there was none. A storage that had finished its conversion stays finished.
    /// </summary>
    public static async Task RestoreMarkerAsync(TableClient comments, TableEntity? found, CancellationToken ct)
    {
        if (found is not null)
        {
            // Its own columns only: Timestamp and the ETag are storage's.
            var row = new TableEntity(found.PartitionKey, found.RowKey);
            foreach (var (name, value) in found)
                if (name is not ("PartitionKey" or "RowKey" or "Timestamp" or "odata.etag")) row[name] = value;
            await comments.UpsertEntityAsync(row, TableUpdateMode.Replace, ct);
        }
        else
            await comments.DeleteEntityAsync(
                API.Services.ActivityNoteConversion.MarkerPartitionKey, API.Services.ActivityNoteConversion.MarkerRowKey, cancellationToken: ct);
    }
}
