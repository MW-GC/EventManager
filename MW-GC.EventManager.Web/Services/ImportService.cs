using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace MW_GC.EventManager.Web.Services;

/// <summary>
/// Posts a legacy JSON export to the API's <c>/api/import</c> endpoint. The five entity
/// arrays are assembled into the combined payload the endpoint expects; each may be empty.
/// </summary>
public sealed class ImportService(HttpClient http)
{
    public async Task<ImportOutcome> ImportAsync(JsonObject payload, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("api/import", payload, ct);

        if (response.IsSuccessStatusCode)
        {
            var summary = await response.Content.ReadFromJsonAsync<ImportSummary>(ct);
            return new ImportOutcome(true, summary, null);
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        var message = string.IsNullOrWhiteSpace(body)
            ? $"Import failed ({(int)response.StatusCode})."
            : body.Trim().Trim('"');
        return new ImportOutcome(false, null, message);
    }
}

/// <summary>Per-collection counts returned by the import endpoint.</summary>
public sealed record ImportSummary(int Games, int Activities, int Events, int Themes, int Holidays);

/// <summary>Result of an import attempt: either a <see cref="Summary"/> or an <see cref="Error"/>.</summary>
public sealed record ImportOutcome(bool Success, ImportSummary? Summary, string? Error);
