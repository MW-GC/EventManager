using System.Net.Http.Json;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Web.Services;

/// <summary>
/// The Series routes (#204, ADR 0002): the create and the cancel. The create stores the Series under its
/// <c>Idempotency-Key</c> and makes its Occurrences at once, so a retry with the same key never makes a second one.
/// </summary>
public sealed class SeriesService(HttpClient http)
{
    /// <summary>
    /// Cancels the Series: the API deletes it and every future Occurrence of it that is not played. Cancel has
    /// only the one Series-wide scope; one Occurrence is cancelled through the Event delete.
    /// </summary>
    public Task<HttpResponseMessage> DeleteAsync(Guid seriesId) => http.DeleteAsync($"api/series/{seriesId}?scope=all");

    public async Task<HttpResponseMessage> CreateAsync(SeriesEntity series, Guid idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/series")
        {
            Content = JsonContent.Create(series)
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey.ToString("D"));
        return await http.SendAsync(request);
    }
}
