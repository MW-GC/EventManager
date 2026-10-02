using System.Net;
using System.Net.Http.Json;

namespace MW_GC.EventManager.Tests;

// A stub API for page tests: routes answer with a fixed status, throw a transport error, or
// return JSON. Unrouted requests answer 404. Every request is recorded.
internal sealed class StubApiHandler : HttpMessageHandler
{
    private readonly List<(HttpMethod Method, string Path, Func<HttpResponseMessage> Respond)> _routes = [];

    public List<(HttpMethod Method, string Path)> Requests { get; } = [];

    public HttpClient Client() => new(this) { BaseAddress = new Uri("https://example.test/") };

    public StubApiHandler Json<T>(HttpMethod method, string path, T body) =>
        On(method, path, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });

    public StubApiHandler Status(HttpMethod method, string path, HttpStatusCode status, string body = "") =>
        On(method, path, () => new HttpResponseMessage(status) { Content = new StringContent(body) });

    public StubApiHandler Offline(HttpMethod method, string path) =>
        On(method, path, () => throw new HttpRequestException("Connection refused (example.test:443)"));

    // Later routes win, so a test can replace an earlier answer (for example a retry after a fix).
    public StubApiHandler On(HttpMethod method, string path, Func<HttpResponseMessage> respond)
    {
        _routes.Insert(0, (method, path, respond));
        return this;
    }

    public int Count(HttpMethod method, string path) => Requests.Count(r => r.Method == method && r.Path == path);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        Requests.Add((request.Method, path));
        foreach (var route in _routes)
            if (route.Method == request.Method && route.Path == path)
                return Task.FromResult(route.Respond());
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
