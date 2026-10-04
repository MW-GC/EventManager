using System.Net;
using System.Net.Http.Json;

namespace MW_GC.EventManager.Web.Tests;

// A stub API for page tests: routes answer with a fixed status, throw a transport error, or
// return JSON. Unrouted requests answer 404. Every request is recorded with its body.
internal sealed class StubApiHandler : HttpMessageHandler
{
    private readonly List<(HttpMethod Method, string Path, Func<Task<HttpResponseMessage>> Respond)> _routes = [];

    public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = [];

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
        _routes.Insert(0, (method, path, () => Task.FromResult(respond())));
        return this;
    }

    // Holds every request to the route until the returned gate is released, then answers with
    // respond: a slow network, so a test can act while the call is still in flight.
    public TaskCompletionSource Hold(HttpMethod method, string path, Func<HttpResponseMessage> respond)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _routes.Insert(0, (method, path, async () => { await gate.Task; return respond(); }));
        return gate;
    }

    public int Count(HttpMethod method, string path) => Requests.Count(r => r.Method == method && r.Path == path);

    // The bodies sent to one route, oldest first.
    public List<string?> Bodies(HttpMethod method, string path) =>
        Requests.Where(r => r.Method == method && r.Path == path).Select(r => r.Body).ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, path, body));
        foreach (var route in _routes)
            if (route.Method == request.Method && route.Path == path)
                return await route.Respond();
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}
