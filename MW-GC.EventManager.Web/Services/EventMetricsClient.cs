using System.Net;
using System.Net.Http.Json;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Web.Services;

/// <summary>
/// An Event's Event metrics on the API (#135, ADR 0001): <c>GET</c> and <c>PUT api/events/{id}/metrics</c>,
/// apart from the Event's own routes. The row's version travels in the ETag header: a read returns it
/// (none when the Event has no row yet, which reads as empty metrics), and a save sends it back as
/// If-Match, or sends no If-Match when there was no row, which only creates one. Never throws for an
/// API failure: the result carries the message, or <see cref="EventMetricsResult.Conflict"/> for a 409.
/// </summary>
public sealed class EventMetricsClient(HttpClient http)
{
    public async Task<EventMetricsResult> GetAsync(Guid eventId)
    {
        try
        {
            using var response = await http.GetAsync($"api/events/{eventId}/metrics");
            return await ReadAsync(response);
        }
        catch (Exception ex) when (ApiResult.IsApiFailure(ex))
        {
            return EventMetricsResult.Failed(ApiResult.FromException(ex));
        }
    }

    /// <summary>Sends every metric, a cleared one as null. <paramref name="eTag"/> is the last tag read or saved.</summary>
    public async Task<EventMetricsResult> SaveAsync(Guid eventId, EventMetricsEntity metrics, string? eTag)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"api/events/{eventId}/metrics")
            {
                Content = JsonContent.Create(new { metrics.Attendees, metrics.NewAttendees, metrics.ReturningAttendees, metrics.AmountSpent })
            };
            // Azure's weak tags (W/"datetime'...'") are sent as read, without .NET's header parsing.
            if (eTag is not null) request.Headers.TryAddWithoutValidation("If-Match", eTag);
            using var response = await http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.Conflict) return EventMetricsResult.Stale;
            return await ReadAsync(response);
        }
        catch (Exception ex) when (ApiResult.IsApiFailure(ex))
        {
            return EventMetricsResult.Failed(ApiResult.FromException(ex));
        }
    }

    private static async Task<EventMetricsResult> ReadAsync(HttpResponseMessage response)
    {
        if (await ApiResult.ErrorAsync(response) is { } error) return EventMetricsResult.Failed(error);
        if (await response.Content.ReadFromJsonAsync<EventMetricsEntity>() is not { } metrics)
            return EventMetricsResult.Failed(ApiResult.UnreadableMessage);
        var eTag = response.Headers.TryGetValues("ETag", out var values) ? values.FirstOrDefault() : null;
        return new(metrics, string.IsNullOrEmpty(eTag) ? null : eTag, null, false);
    }
}

/// <summary>
/// A read or save of Event metrics: the row and its ETag (null when no row is stored), or the
/// failure message, or <see cref="Conflict"/> when someone else saved first (409).
/// </summary>
public sealed record EventMetricsResult(EventMetricsEntity? Metrics, string? ETag, string? Error, bool Conflict)
{
    public static EventMetricsResult Failed(string error) => new(null, null, error, false);
    public static readonly EventMetricsResult Stale = new(null, null, null, true);
}
