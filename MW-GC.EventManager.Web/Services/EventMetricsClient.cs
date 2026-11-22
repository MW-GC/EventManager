using System.Net;
using System.Net.Http.Json;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;

namespace MW_GC.EventManager.Web.Services;

/// <summary>
/// Event metrics on the API (#135, #136, ADR 0001): <c>GET</c> and <c>PUT api/events/{id}/metrics</c> for one
/// Event, apart from the Event's own routes, and <c>GET api/event-metrics</c> for every Event. The row's version
/// travels in the ETag header: a read returns it (none when the Event has no row yet, which reads as empty
/// metrics), and a save sends it back as If-Match, or sends no If-Match when there was no row, which only
/// creates one. <see cref="GetAsync"/>, <see cref="SaveAsync"/> and <see cref="LoadAllAsync"/> never throw for
/// an API failure: each returns a result that says what happened. <see cref="GetAllAsync"/> is the one call
/// that throws; it stays only until its callers move to <see cref="LoadAllAsync"/>.
/// </summary>
public sealed class EventMetricsClient(HttpClient http)
{
    /// <summary>
    /// Every Event's metrics with its Like count (<c>GET api/event-metrics</c>, #136). Throws on failure, like
    /// the other list loads, so <see cref="LoadErrors"/> can name it. Kept for its current callers (the Stats
    /// page and the Activity performance panel); new code uses <see cref="LoadAllAsync"/>, which reads the same
    /// route and returns a result instead.
    /// </summary>
    public Task<List<EventMetricsWithLikes>?> GetAllAsync() => http.GetFromJsonAsync<List<EventMetricsWithLikes>>("api/event-metrics");

    /// <summary>
    /// Every Event's metrics with its Like count (<c>GET api/event-metrics</c>, #136). An Event with neither
    /// metrics nor Likes has no item. Never throws for an API failure.
    /// </summary>
    public async Task<EventMetricsListResult> LoadAllAsync()
    {
        try
        {
            using var response = await http.GetAsync("api/event-metrics");
            if (await ApiResult.ErrorAsync(response) is { } error) return new EventMetricsListResult.Failed(error);
            return await response.Content.ReadFromJsonAsync<List<EventMetricsWithLikes>>() is { } items
                ? new EventMetricsListResult.Loaded(items)
                : new EventMetricsListResult.Failed(ApiResult.UnreadableMessage);
        }
        catch (Exception ex) when (ApiResult.IsApiFailure(ex))
        {
            return new EventMetricsListResult.Failed(ApiResult.FromException(ex));
        }
    }

    public async Task<EventMetricsResult> GetAsync(Guid eventId)
    {
        try
        {
            using var response = await http.GetAsync($"api/events/{eventId}/metrics");
            return await ReadAsync(response);
        }
        catch (Exception ex) when (ApiResult.IsApiFailure(ex))
        {
            return new EventMetricsResult.Failed(ApiResult.FromException(ex));
        }
    }

    /// <summary>
    /// Sends the metrics entity itself, so every metric it has reaches the API, a cleared one as null.
    /// <paramref name="eTag"/> is the last tag read or saved.
    /// </summary>
    public async Task<EventMetricsResult> SaveAsync(Guid eventId, EventMetricsEntity metrics, string? eTag)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"api/events/{eventId}/metrics")
            {
                Content = JsonContent.Create(metrics)
            };
            // Azure's weak tags (W/"datetime'...'") are sent as read, without .NET's header parsing.
            if (eTag is not null) request.Headers.TryAddWithoutValidation("If-Match", eTag);
            using var response = await http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.Conflict) return EventMetricsResult.Stale.Instance;
            return await ReadAsync(response);
        }
        catch (Exception ex) when (ApiResult.IsApiFailure(ex))
        {
            return new EventMetricsResult.Failed(ApiResult.FromException(ex));
        }
    }

    private static async Task<EventMetricsResult> ReadAsync(HttpResponseMessage response)
    {
        if (await ApiResult.ErrorAsync(response) is { } error) return new EventMetricsResult.Failed(error);
        if (await response.Content.ReadFromJsonAsync<EventMetricsEntity>() is not { } metrics)
            return new EventMetricsResult.Failed(ApiResult.UnreadableMessage);
        var eTag = response.Headers.TryGetValues("ETag", out var values) ? values.FirstOrDefault() : null;
        return new EventMetricsResult.Stored(metrics, string.IsNullOrEmpty(eTag) ? null : eTag);
    }
}

/// <summary>A read or save of one Event's metrics: exactly one of the three outcomes below.</summary>
public abstract record EventMetricsResult
{
    private EventMetricsResult() { }

    /// <summary>The row as stored, with its ETag (null when no row is stored yet).</summary>
    public sealed record Stored(EventMetricsEntity Metrics, string? ETag) : EventMetricsResult;

    /// <summary>The request failed; the message says why.</summary>
    public sealed record Failed(string Error) : EventMetricsResult;

    /// <summary>Someone else saved first (409): the tag sent was stale, or a first save lost the race.</summary>
    public sealed record Stale : EventMetricsResult
    {
        public static readonly Stale Instance = new();
    }
}

/// <summary>The read of every Event's metrics: the items, or the failure message.</summary>
public abstract record EventMetricsListResult
{
    private EventMetricsListResult() { }

    public sealed record Loaded(List<EventMetricsWithLikes> Items) : EventMetricsListResult;

    public sealed record Failed(string Error) : EventMetricsListResult;
}
