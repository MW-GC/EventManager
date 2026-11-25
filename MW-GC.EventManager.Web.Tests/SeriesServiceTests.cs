using System.Net;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Tests;

// #204: a Series cancel is DELETE api/series/{id}?scope=all, exactly: the API refuses any other scope (400), and the
// stub page tests match on the path alone, so the query string is pinned here.
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

    private sealed class Recorder : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Uri, string? Body)> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Add((request.Method, request.RequestUri!.AbsoluteUri, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
