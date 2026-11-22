using System.Net;
using Azure.Data.Tables;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// The two environment variables of the tests that run against real Table Storage (Azurite), and the
/// guard that keeps them on local Azurite. Both variables are optional, so <c>dotnet test</c> on the
/// solution needs no emulator: without <see cref="ConnectionVariable"/> those tests are reported as
/// skipped (inconclusive). They write, delete and convert rows, so they also skip, before anything is
/// contacted, when the connection string does not point at local Azurite (<see cref="IsLocalAzurite"/>).
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
        if (WhyNotLocal(connection) is { } reason)
            Assert.Inconclusive(NotLocalMessage(reason));
        return connection;
    }

    /// <summary>Why an Azurite test skipped a connection string that is set but not local Azurite.</summary>
    public static string NotLocalMessage(string reason) =>
        $"Azurite test skipped: {ConnectionVariable} does not point at local Azurite: {reason}. These tests write, " +
        "delete and convert rows, so they run only when the Tables client it builds talks to a loopback host " +
        "(127.0.0.1, localhost or ::1).";

    /// <summary>
    /// Whether a connection string points at local Azurite: the Tables client built from it would talk to
    /// a loopback host (<c>127.0.0.1</c>, <c>localhost</c>, <c>::1</c>). See <see cref="WhyNotLocal"/>.
    /// </summary>
    public static bool IsLocalAzurite(string? connection) => WhyNotLocal(connection) is null;

    /// <summary>
    /// Null when the connection string points at local Azurite, otherwise why not. The decision is made on
    /// the endpoint of a <see cref="TableServiceClient"/> built from the string, the same way the tests
    /// build theirs, not on its text: <c>UseDevelopmentStorage=true</c> with a remote
    /// <c>DevelopmentStorageProxyUri</c>, or the <c>devstoreaccount1</c> account with a remote
    /// <c>TableEndpoint</c>, would otherwise pass a text check and send the tests' writes elsewhere.
    /// Building the client contacts nothing. A string the client cannot read is not local.
    /// </summary>
    public static string? WhyNotLocal(string? connection)
    {
        if (string.IsNullOrWhiteSpace(connection)) return "it is blank";
        Uri endpoint;
        try
        {
            endpoint = new TableServiceClient(connection).Uri;
        }
        catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException)
        {
            return $"it cannot be read as a connection string ({e.GetType().Name})";
        }
        return IsLoopback(endpoint.Host) ? null : $"its Tables client would talk to {endpoint.Host}, which is not a loopback host";
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
