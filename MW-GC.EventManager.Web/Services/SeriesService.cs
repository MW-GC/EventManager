using System.Net.Http.Json;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Web.Services;

/// <summary>
/// The Series routes (#204, ADR 0002). Only the create for now: the Series stores itself under its
/// <c>Idempotency-Key</c> and makes its Occurrences at once, so a retry with the same key never makes a second one.
/// </summary>
public sealed class SeriesService(HttpClient http)
{
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
