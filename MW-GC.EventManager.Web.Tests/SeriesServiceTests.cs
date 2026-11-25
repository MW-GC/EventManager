using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Tests;

// #204: a Series cancel is DELETE api/series/{id}?scope=all, exactly: the API refuses any other scope (400), and the
// stub page tests match on the path alone, so the query string is pinned here. The edit's read (GET with its ETag) and
// write (PUT with the scope, the Occurrence and If-Match) are pinned here too.
[TestClass]
public sealed class SeriesServiceTests
{
    [TestMethod]
    public async Task DeleteSendsADeleteWithScopeAllForThatSeries()
    {
        var series = Guid.Parse("c0ffee00-1234-4321-abcd-0123456789ab");
        var recorder = new Recorder();
        using var http = new HttpClient(recorder) { BaseAddress = new Uri("https://example.test/") };

        using var response = await new SeriesService(http).DeleteAsync(series);

        var sent = Assert.ContainsSingle(recorder.Sent);
        Assert.AreEqual(HttpMethod.Delete, sent.Method);
        Assert.AreEqual("https://example.test/api/series/c0ffee00-1234-4321-abcd-0123456789ab?scope=all", sent.Uri);
        Assert.IsNull(sent.Body, "a cancel sends no body");
    }

    // #204 edit: GET api/series/{id} returns the Series and its ETag header exactly as sent.
    [TestMethod]
    public async Task GetReadsTheSeriesAndItsETag()
    {
        const string tag = "W/\"datetime'2026-11-25T10%3A00%3A00.0000000Z'\"";
        var recorder = new Recorder(() =>
        {
            var answer = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new SeriesEntity { Id = SeriesId, Name = "Weekly raid", SlotCount = 4 }) };
            answer.Headers.TryAddWithoutValidation("ETag", tag);
            return answer;
        });
        using var http = new HttpClient(recorder) { BaseAddress = new Uri("https://example.test/") };

        var read = await new SeriesService(http).GetAsync(SeriesId);

        var sent = Assert.ContainsSingle(recorder.Sent);
        Assert.AreEqual((HttpMethod.Get, $"https://example.test/api/series/{SeriesId}", (string?)null), (sent.Method, sent.Uri, sent.Body));
        var stored = Assert.IsInstanceOfType<SeriesRead.Stored>(read);
        Assert.AreEqual((SeriesId, "Weekly raid", 4, tag), (stored.Series.Id, stored.Series.Name, stored.Series.SlotCount, stored.ETag));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.NotFound, "Not found. It may have been deleted.")]
    [DataRow(HttpStatusCode.ServiceUnavailable, "The server had a problem (HTTP 503). Try again.")]
    public async Task AFailedGetSaysWhy(HttpStatusCode status, string message)
    {
        using var http = new HttpClient(new Recorder(() => new HttpResponseMessage(status))) { BaseAddress = new Uri("https://example.test/") };

        Assert.AreEqual(new SeriesRead.Failed(message), await new SeriesService(http).GetAsync(SeriesId));
    }

    [TestMethod]
    public async Task AGetThatCannotReachTheServerSaysSo()
    {
        using var http = new HttpClient(new Recorder(() => throw new HttpRequestException("refused"))) { BaseAddress = new Uri("https://example.test/") };

        Assert.AreEqual(new SeriesRead.Failed("Could not reach the server. Check the connection and try again."), await new SeriesService(http).GetAsync(SeriesId));
    }

    // #204 edit: PUT api/series/{id} with the scope in the query, the Occurrence for "following", If-Match as read (a weak
    // Azure tag, not reparsed), and the Series as the JSON body.
    [TestMethod]
    [DataRow(SeriesScope.All, "scope=all")]
    [DataRow(SeriesScope.Following, "scope=following&occurrence=0c0c0000-0000-0000-0000-000000000009")]
    public async Task UpdateSendsAPutWithTheScope_TheIfMatchRead_AndTheSeries(SeriesScope scope, string query)
    {
        const string tag = "W/\"datetime'2026-11-25T10%3A00%3A00.0000000Z'\"";
        var occurrence = Guid.Parse("0c0c0000-0000-0000-0000-000000000009");
        var recorder = new Recorder();
        using var http = new HttpClient(recorder) { BaseAddress = new Uri("https://example.test/") };

        using var response = await new SeriesService(http).UpdateAsync(new SeriesEntity { Id = SeriesId, Name = "Raid night", SlotCount = 4 }, tag, scope, occurrence);

        var sent = Assert.ContainsSingle(recorder.Sent);
        Assert.AreEqual(HttpMethod.Put, sent.Method);
        Assert.AreEqual($"https://example.test/api/series/{SeriesId}?{query}", sent.Uri);
        Assert.AreEqual(tag, sent.IfMatch);
        var body = JsonSerializer.Deserialize<SeriesEntity>(sent.Body!, JsonSerializerOptions.Web)!;
        Assert.AreEqual((SeriesId, "Raid night", 4), (body.Id, body.Name, body.SlotCount));
    }

    // An update without the eTag read would be the last writer winning over another admin's change: it is never sent.
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    public async Task UpdateWithNoETag_SendsNothing(string? eTag)
    {
        var recorder = new Recorder();
        using var http = new HttpClient(recorder) { BaseAddress = new Uri("https://example.test/") };

        await Assert.ThrowsAsync<ArgumentException>(() => new SeriesService(http).UpdateAsync(new SeriesEntity { Id = SeriesId }, eTag!, SeriesScope.All, Guid.NewGuid()));

        Assert.IsEmpty(recorder.Sent);
    }

    private static readonly Guid SeriesId = Guid.Parse("c0ffee00-1234-4321-abcd-0123456789ab");

    private sealed class Recorder(Func<HttpResponseMessage>? answer = null) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Uri, string? Body, string? IfMatch)> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Add((request.Method, request.RequestUri!.AbsoluteUri, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.TryGetValues("If-Match", out var tags) ? string.Join(", ", tags) : null));
            return answer?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
