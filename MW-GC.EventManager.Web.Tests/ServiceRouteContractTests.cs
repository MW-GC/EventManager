using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.Azure.Functions.Worker;
using MW_GC.EventManager.API.Functions;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Tests;

// Issue #57: the route contract between the Web services and the API. One table lists every public
// method of every service, and of the shared LibraryClient once per list it serves (#125), with the
// HTTP method and relative path it must send. The tests
// call each method through a stub API and compare, fail when a service has a public method the
// table does not list, and check every row against the HttpTrigger routes the API declares.
[TestClass]
public sealed class ServiceRouteContractTests
{
    private static readonly Guid Id = Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");
    private static readonly Guid CommentId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private sealed record Row(Type Service, string Method, string Verb, string Path, Func<HttpClient, Task> Call)
    {
        public override string ToString() => $"{Display(Service)}.{Method} -> {Verb} {Path}";
    }

    // LibraryClient<GameEntity> and LibraryClient<ThemeEntity> share the name LibraryClient`1, so name the item type too.
    private static string Display(Type service) => service.IsGenericType
        ? $"{service.Name[..service.Name.IndexOf('`')]}<{string.Join(",", service.GetGenericArguments().Select(a => a.Name))}>"
        : service.Name;

    // {id} and {commentId} stand for the Guids the call passes; the API declares the same segments as {id:guid}
    // and {commentId:guid}.
    private static readonly Row[] Table =
    [
        // Every Games read goes through LibraryClient<GameEntity> below (#196.1).

        // Activities read, create, update and delete through the shared client (#126); the service keeps the one
        // route only Activities have.
        new(typeof(ActivityService), nameof(ActivityService.DuplicateAsync), "POST", "api/activities/{id}/duplicate", h => new ActivityService(h).DuplicateAsync(Id)),

        new(typeof(EventService), nameof(EventService.GetAllAsync), "GET", "api/events", h => new EventService(h).GetAllAsync()),
        new(typeof(EventService), nameof(EventService.GetAsync), "GET", "api/events/{id}", h => new EventService(h).GetAsync(Id)),
        new(typeof(EventService), nameof(EventService.SaveAsync), "POST", "api/events", h => new EventService(h).SaveAsync(new EventEntity { Id = Id }, Guid.NewGuid())),
        new(typeof(EventService), nameof(EventService.UpdateAsync), "PUT", "api/events/{id}", h => new EventService(h).UpdateAsync(new EventEntity { Id = Id })),
        new(typeof(EventService), nameof(EventService.DeleteAsync), "DELETE", "api/events/{id}", h => new EventService(h).DeleteAsync(Id)),
        new(typeof(EventService), nameof(EventService.GetFeedbackLinkAsync), "GET", "api/events/{id}/feedback-link", h => new EventService(h).GetFeedbackLinkAsync(Id)),
        new(typeof(EventService), nameof(EventService.GetLikesAsync), "GET", "api/events/{id}/likes", h => new EventService(h).GetLikesAsync(Id)),

        // A Series and its Occurrences (#204): the create, the read, the edit and the cancel. A scope is a query string;
        // the API route is matched without it. The edit's "following" query is pinned in SeriesServiceTests.
        new(typeof(SeriesService), nameof(SeriesService.CreateAsync), "POST", "api/series", h => new SeriesService(h).CreateAsync(new SeriesEntity { Id = Id }, Guid.NewGuid())),
        new(typeof(SeriesService), nameof(SeriesService.GetAsync), "GET", "api/series/{id}", h => new SeriesService(h).GetAsync(Id)),
        new(typeof(SeriesService), nameof(SeriesService.UpdateAsync), "PUT", "api/series/{id}?scope=all", h => new SeriesService(h).UpdateAsync(new SeriesEntity { Id = Id }, null, SeriesScope.All, Guid.NewGuid())),
        new(typeof(SeriesService), nameof(SeriesService.DeleteAsync), "DELETE", "api/series/{id}?scope=all", h => new SeriesService(h).DeleteAsync(Id)),

        new(typeof(EventMetricsClient), nameof(EventMetricsClient.LoadAllAsync), "GET", "api/event-metrics", h => new EventMetricsClient(h).LoadAllAsync()),
        new(typeof(EventMetricsClient), nameof(EventMetricsClient.GetAsync), "GET", "api/events/{id}/metrics", h => new EventMetricsClient(h).GetAsync(Id)),
        new(typeof(EventMetricsClient), nameof(EventMetricsClient.SaveAsync), "PUT", "api/events/{id}/metrics", h => new EventMetricsClient(h).SaveAsync(Id, new EventMetricsEntity(), null)),

        // An Activity's Comments, each its own row under its Activity (#131, ADR 0001).
        new(typeof(CommentClient), nameof(CommentClient.GetAllAsync), "GET", "api/activities/{id}/comments", h => new CommentClient(h).GetAllAsync(Id)),
        new(typeof(CommentClient), nameof(CommentClient.AddAsync), "POST", "api/activities/{id}/comments", h => new CommentClient(h).AddAsync(Id, "Ran long")),
        new(typeof(CommentClient), nameof(CommentClient.UpdateAsync), "PUT", "api/activities/{id}/comments/{commentId}", h => new CommentClient(h).UpdateAsync(Id, CommentId, "Ran long", null)),
        new(typeof(CommentClient), nameof(CommentClient.DeleteAsync), "DELETE", "api/activities/{id}/comments/{commentId}", h => new CommentClient(h).DeleteAsync(Id, CommentId, null)),

        // The shared Library client, once per list it serves, as the app registers it (AddLibraryClients, called by
        // Program.cs), so a wrong route in the registration fails here. Every path is written out. It has no get-one
        // call: no Library page reads one item.
        .. LibraryRows<GameEntity>("api/games", "api/games/{id}"),
        .. LibraryRows<ThemeEntity>("api/themes", "api/themes/{id}"),
        .. LibraryRows<HolidayEntity>("api/holidays", "api/holidays/{id}"),
        .. LibraryRows<ActivityEntity>("api/activities", "api/activities/{id}"),
    ];

    private static Row[] LibraryRows<T>(string list, string one) where T : EntityBase, new() =>
    [
        // Each call resolves the client from the app's registration over the row's HttpClient, in a provider of its
        // own that is disposed once the call is done (LibraryServices, shared with LibraryClientTests).
        new(typeof(LibraryClient<T>), nameof(LibraryClient<T>.GetAllAsync), "GET", list, h => LibraryServices.CallAsync<T, List<T>?>(h, c => c.GetAllAsync())),
        new(typeof(LibraryClient<T>), nameof(LibraryClient<T>.CreateAsync), "POST", list, h => LibraryServices.CallAsync<T, string?>(h, c => c.CreateAsync(new T { Id = Id }))),
        new(typeof(LibraryClient<T>), nameof(LibraryClient<T>.UpdateAsync), "PUT", one, h => LibraryServices.CallAsync<T, string?>(h, c => c.UpdateAsync(new T { Id = Id }))),
        new(typeof(LibraryClient<T>), nameof(LibraryClient<T>.DeleteAsync), "DELETE", one, h => LibraryServices.CallAsync<T, string?>(h, c => c.DeleteAsync(Id))),
    ];

    private static readonly Type[] Services =
    [
        typeof(ActivityService), typeof(EventService), typeof(SeriesService), typeof(EventMetricsClient), typeof(CommentClient),
        typeof(LibraryClient<GameEntity>), typeof(LibraryClient<ThemeEntity>), typeof(LibraryClient<HolidayEntity>),
        typeof(LibraryClient<ActivityEntity>)
    ];

    [TestMethod]
    public async Task EveryServiceMethodSendsTheMethodAndPathInTheTable()
    {
        var wrong = new List<string>();
        foreach (var row in Table)
        {
            var (client, recorder) = Client(row);
            await row.Call(client);

            var expected = $"{row.Verb} {Concrete(row.Path)}";
            var sent = recorder.Sent.Select(r => $"{r.Method.Method} {r.Path}").ToList();
            if (sent.Count != 1 || sent[0] != expected)
                wrong.Add($"{Display(row.Service)}.{row.Method}: expected {expected}, sent [{string.Join(", ", sent)}]");
        }
        Assert.IsEmpty(wrong, "Service calls that do not match the route table:\n" + string.Join("\n", wrong));
    }

    [TestMethod]
    public void EveryPublicServiceMethodHasARowInTheTable()
    {
        // Names, not signatures: an added overload adds a second name and fails too.
        var declared = Services
            .SelectMany(s => s.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName)
                .Select(m => $"{Display(s)}.{m.Name}"))
            .Order().ToList();
        var listed = Table.Select(r => $"{Display(r.Service)}.{r.Method}").Order().ToList();

        var missing = declared.ToList();
        foreach (var name in listed) missing.Remove(name);
        var extra = listed.ToList();
        foreach (var name in declared) extra.Remove(name);

        Assert.IsEmpty(missing, "Public service methods with no row in the route table: " + string.Join(", ", missing));
        Assert.IsEmpty(extra, "Route table rows with no public service method: " + string.Join(", ", extra));
    }

    [TestMethod]
    public async Task EventSaveSendsTheIdempotencyKeyHeader()
    {
        var key = Guid.NewGuid();
        var (client, recorder) = Client(Table.Single(r => r.Service == typeof(EventService) && r.Method == nameof(EventService.SaveAsync)));

        await new EventService(client).SaveAsync(new EventEntity { Id = Id }, key);

        var request = Assert.ContainsSingle(recorder.Sent);
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("api/events", request.Path);
        Assert.AreEqual(key.ToString("D"), Assert.ContainsSingle(request.IdempotencyKey));
    }

    // #204: a Series create sends its key exactly as an Event create does.
    [TestMethod]
    public async Task SeriesCreateSendsTheIdempotencyKeyHeader()
    {
        var key = Guid.NewGuid();
        var (client, recorder) = Client(Table.Single(r => r.Service == typeof(SeriesService) && r.Method == nameof(SeriesService.CreateAsync)));

        await new SeriesService(client).CreateAsync(new SeriesEntity { Id = Id }, key);

        var request = Assert.ContainsSingle(recorder.Sent);
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("api/series", request.Path);
        Assert.AreEqual(key.ToString("D"), Assert.ContainsSingle(request.IdempotencyKey));
    }

    [TestMethod]
    public void EveryRowMatchesARouteTheApiDeclares()
    {
        var routes = ApiRoutes();
        // Guard the reflection itself: if it found nothing, every row would fail for the wrong reason.
        Assert.IsNotEmpty(routes);

        var unmatched = Table
            .Where(r => !routes.Contains((r.Verb, PathOnly(r.Path).Replace("{id}", "{id:guid}").Replace("{commentId}", "{commentId:guid}"))))
            .Select(r => r.ToString())
            .ToList();
        Assert.IsEmpty(unmatched, "Route table rows with no matching API HttpTrigger:\n" + string.Join("\n", unmatched));
    }

    // #133: the old single-text PATCH is gone from the API. The Comment routes on the same path stay.
    [TestMethod]
    public void TheApiNoLongerDeclaresTheOldCommentsPatch()
    {
        var routes = ApiRoutes();

        Assert.DoesNotContain(("PATCH", "api/activities/{id:guid}/comments"), routes);
        Assert.Contains(("GET", "api/activities/{id:guid}/comments"), routes);
        Assert.Contains(("POST", "api/activities/{id:guid}/comments"), routes);
        Assert.Contains(("PUT", "api/activities/{id:guid}/comments/{commentId:guid}"), routes);
        Assert.Contains(("DELETE", "api/activities/{id:guid}/comments/{commentId:guid}"), routes);
    }

    // Every (METHOD, api/route) pair declared by an HttpTrigger in the API assembly. The Function
    // classes are internal; reflection reads them all the same. host.json sets no routePrefix, so
    // the Functions default, "api", applies.
    private static HashSet<(string Verb, string Path)> ApiRoutes()
    {
        var routes = new HashSet<(string, string)>();
        foreach (var type in typeof(GameFunctions).Assembly.GetTypes())
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        foreach (var parameter in method.GetParameters())
        {
            if (parameter.GetCustomAttribute<HttpTriggerAttribute>() is not { } trigger) continue;
            Assert.IsNotNull(trigger.Route, $"{type.Name}.{method.Name} has an HttpTrigger with no Route");
            Assert.IsNotNull(trigger.Methods, $"{type.Name}.{method.Name} has an HttpTrigger with no Methods");
            foreach (var verb in trigger.Methods)
                routes.Add((verb.ToUpperInvariant(), $"api/{trigger.Route}"));
        }
        return routes;
    }

    // The row's path with the Guids the call passes.
    private static string Concrete(string path) =>
        path.Replace("{id}", Id.ToString("D")).Replace("{commentId}", CommentId.ToString("D"));

    // A row's path without its query string (such as a Series cancel's ?scope=all): what an HttpTrigger route declares.
    private static string PathOnly(string path) => path.Split('?')[0];

    // A stub API that answers the row's own route with JSON null (so the Get methods return
    // instead of throwing on a 404), behind a handler that records what was sent.
    private static (HttpClient Client, RequestRecorder Recorder) Client(Row row)
    {
        var api = new StubApiHandler();
        api.On(new HttpMethod(row.Verb), "/" + Concrete(PathOnly(row.Path)),
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null", Encoding.UTF8, "application/json") });
        var recorder = new RequestRecorder(api);
        return (new HttpClient(recorder) { BaseAddress = new Uri("https://example.test/") }, recorder);
    }

    // Records each request's method, path relative to the base address (query string included) and
    // Idempotency-Key header values, then passes it on.
    private sealed class RequestRecorder(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private static readonly Uri Base = new("https://example.test/");

        public List<(HttpMethod Method, string Path, List<string> IdempotencyKey)> Sent { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var keys = request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.ToList() : [];
            Sent.Add((request.Method, Base.MakeRelativeUri(request.RequestUri!).ToString(), keys));
            return base.SendAsync(request, cancellationToken);
        }
    }
}
