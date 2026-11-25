using System.Net.Http.Json;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Web.Services;

/// <summary>The two Series-wide scopes of an edit (#204); one Occurrence is edited through the Event route.</summary>
public enum SeriesScope
{
    /// <summary><c>?scope=all</c>: the Series and every future Occurrence not played or edited by hand.</summary>
    All,
    /// <summary><c>?scope=following&amp;occurrence={eventId}</c>: that Occurrence and the later ones, as a new Series.</summary>
    Following,
}

/// <summary>
/// The Series routes (#204, ADR 0002): the create, the read, the edit and the cancel. The create stores the Series under
/// its <c>Idempotency-Key</c> and makes its Occurrences at once, so a retry with the same key never makes a second one.
/// The read returns the Series with its version (the <c>ETag</c> header), which an edit sends back as <c>If-Match</c>, so
/// a Series changed meanwhile is a 409 instead of a silent overwrite.
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

    /// <summary>The Series as stored, with its ETag. Never throws for an API failure: the result says what happened.</summary>
    public async Task<SeriesRead> GetAsync(Guid seriesId)
    {
        try
        {
            using var response = await http.GetAsync($"api/series/{seriesId}");
            if (await ApiResult.ErrorAsync(response) is { } error) return new SeriesRead.Failed(error);
            if (await response.Content.ReadFromJsonAsync<SeriesEntity>() is not { } series)
                return new SeriesRead.Failed(ApiResult.UnreadableMessage);
            var eTag = response.Headers.TryGetValues("ETag", out var values) ? values.FirstOrDefault() : null;
            return new SeriesRead.Stored(series, string.IsNullOrEmpty(eTag) ? null : eTag);
        }
        catch (Exception ex) when (ApiResult.IsApiFailure(ex))
        {
            return new SeriesRead.Failed(ApiResult.FromException(ex));
        }
    }

    /// <summary>
    /// Edits the Series <paramref name="series"/> names with <paramref name="scope"/>; <see cref="SeriesScope.Following"/>
    /// starts the change at <paramref name="occurrenceId"/>, the Occurrence the admin edited. <paramref name="eTag"/> is
    /// the one <see cref="GetAsync"/> read, sent as <c>If-Match</c> as read (Azure's weak tags are not .NET-parsed).
    /// </summary>
    public async Task<HttpResponseMessage> UpdateAsync(SeriesEntity series, string? eTag, SeriesScope scope, Guid occurrenceId)
    {
        var query = scope == SeriesScope.Following ? $"scope=following&occurrence={occurrenceId:D}" : "scope=all";
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/series/{series.Id}?{query}")
        {
            Content = JsonContent.Create(series)
        };
        if (eTag is not null) request.Headers.TryAddWithoutValidation("If-Match", eTag);
        return await http.SendAsync(request);
    }
}

/// <summary>A read of one Series: the Series with its ETag, or the failure message.</summary>
public abstract record SeriesRead
{
    private SeriesRead() { }

    public sealed record Stored(SeriesEntity Series, string? ETag) : SeriesRead;

    public sealed record Failed(string Error) : SeriesRead;
}
