using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.Azure.Functions.Worker;
using MW_GC.EventManager.API.Functions;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Tests;

// Issue #57: the route contract between the five Web services and the API. One table lists every
// public method of every service with the HTTP method and relative path it must send. The tests
// call each method through a stub API and compare, fail when a service has a public method the
// table does not list, and check every row against the HttpTrigger routes the API declares.
[TestClass]
public sealed class ServiceRouteContractTests
{
    private static readonly Guid Id = Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");

    private sealed record Row(Type Service, string Method, string Verb, string Path, Func<HttpClient, Task> Call)
    {
        public override string ToString() => $"{Service.Name}.{Method} -> {Verb} {Path}";
    }

    // {id} stands for the Guid the call passes; the API declares the same segment as {id:guid}.
    private static readonly Row[] Table =
    [
        new(typeof(GameService), nameof(GameService.GetAllAsync), "GET", "api/games", h => new GameService(h).GetAllAsync()),
        new(typeof(GameService), nameof(GameService.GetAsync), "GET", "api/games/{id}", h => new GameService(h).GetAsync(Id)),
        new(typeof(GameService), nameof(GameService.CreateAsync), "POST", "api/games", h => new GameService(h).CreateAsync(new GameEntity { Id = Id })),
        new(typeof(GameService), nameof(GameService.UpdateAsync), "PUT", "api/games/{id}", h => new GameService(h).UpdateAsync(new GameEntity { Id = Id })),
        new(typeof(GameService), nameof(GameService.DeleteAsync), "DELETE", "api/games/{id}", h => new GameService(h).DeleteAsync(Id)),

        new(typeof(ActivityService), nameof(ActivityService.GetAllAsync), "GET", "api/activities", h => new ActivityService(h).GetAllAsync()),
        new(typeof(ActivityService), nameof(ActivityService.GetAsync), "GET", "api/activities/{id}", h => new ActivityService(h).GetAsync(Id)),
        new(typeof(ActivityService), nameof(ActivityService.CreateAsync), "POST", "api/activities", h => new ActivityService(h).CreateAsync(new ActivityEntity { Id = Id })),
        new(typeof(ActivityService), nameof(ActivityService.UpdateAsync), "PUT", "api/activities/{id}", h => new ActivityService(h).UpdateAsync(new ActivityEntity { Id = Id })),
        new(typeof(ActivityService), nameof(ActivityService.DeleteAsync), "DELETE", "api/activities/{id}", h => new ActivityService(h).DeleteAsync(Id)),
        new(typeof(ActivityService), nameof(ActivityService.DuplicateAsync), "POST", "api/activities/{id}/duplicate", h => new ActivityService(h).DuplicateAsync(Id)),
        new(typeof(ActivityService), nameof(ActivityService.UpdateCommentsAsync), "PATCH", "api/activities/{id}/comments", h => new ActivityService(h).UpdateCommentsAsync(Id, "Note")),

        new(typeof(EventService), nameof(EventService.GetAllAsync), "GET", "api/events", h => new EventService(h).GetAllAsync()),
        new(typeof(EventService), nameof(EventService.GetAsync), "GET", "api/events/{id}", h => new EventService(h).GetAsync(Id)),
        new(typeof(EventService), nameof(EventService.SaveAsync), "POST", "api/events", h => new EventService(h).SaveAsync(new EventEntity { Id = Id }, Guid.NewGuid())),
        new(typeof(EventService), nameof(EventService.UpdateAsync), "PUT", "api/events/{id}", h => new EventService(h).UpdateAsync(new EventEntity { Id = Id })),
        new(typeof(EventService), nameof(EventService.DeleteAsync), "DELETE", "api/events/{id}", h => new EventService(h).DeleteAsync(Id)),

        new(typeof(ThemeService), nameof(ThemeService.GetAllAsync), "GET", "api/themes", h => new ThemeService(h).GetAllAsync()),
        new(typeof(ThemeService), nameof(ThemeService.CreateAsync), "POST", "api/themes", h => new ThemeService(h).CreateAsync(new ThemeEntity { Id = Id })),
        new(typeof(ThemeService), nameof(ThemeService.UpdateAsync), "PUT", "api/themes/{id}", h => new ThemeService(h).UpdateAsync(new ThemeEntity { Id = Id })),
        new(typeof(ThemeService), nameof(ThemeService.DeleteAsync), "DELETE", "api/themes/{id}", h => new ThemeService(h).DeleteAsync(Id)),

        new(typeof(HolidayService), nameof(HolidayService.GetAllAsync), "GET", "api/holidays", h => new HolidayService(h).GetAllAsync()),
        new(typeof(HolidayService), nameof(HolidayService.CreateAsync), "POST", "api/holidays", h => new HolidayService(h).CreateAsync(new HolidayEntity { Id = Id })),
        new(typeof(HolidayService), nameof(HolidayService.UpdateAsync), "PUT", "api/holidays/{id}", h => new HolidayService(h).UpdateAsync(new HolidayEntity { Id = Id })),
        new(typeof(HolidayService), nameof(HolidayService.DeleteAsync), "DELETE", "api/holidays/{id}", h => new HolidayService(h).DeleteAsync(Id)),
    ];

    private static readonly Type[] Services = [typeof(GameService), typeof(ActivityService), typeof(EventService), typeof(ThemeService), typeof(HolidayService)];

    [TestMethod]
    public async Task EveryServiceMethodSendsTheMethodAndPathInTheTable()
    {
        var wrong = new List<string>();
        foreach (var row in Table)
        {
            var (client, recorder) = Client(row);
            await row.Call(client);

            var expected = $"{row.Verb} {row.Path.Replace("{id}", Id.ToString("D"))}";
            var sent = recorder.Sent.Select(r => $"{r.Method.Method} {r.Path}").ToList();
            if (sent.Count != 1 || sent[0] != expected)
                wrong.Add($"{row.Service.Name}.{row.Method}: expected {expected}, sent [{string.Join(", ", sent)}]");
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
                .Select(m => $"{s.Name}.{m.Name}"))
            .Order().ToList();
        var listed = Table.Select(r => $"{r.Service.Name}.{r.Method}").Order().ToList();

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

    [TestMethod]
    public void EveryRowMatchesARouteTheApiDeclares()
    {
        var routes = ApiRoutes();
        // Guard the reflection itself: if it found nothing, every row would fail for the wrong reason.
        Assert.IsNotEmpty(routes);

        var unmatched = Table
            .Where(r => !routes.Contains((r.Verb, r.Path.Replace("{id}", "{id:guid}"))))
            .Select(r => r.ToString())
            .ToList();
        Assert.IsEmpty(unmatched, "Route table rows with no matching API HttpTrigger:\n" + string.Join("\n", unmatched));
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

    // A stub API that answers the row's own route with JSON null (so the Get methods return
    // instead of throwing on a 404), behind a handler that records what was sent.
    private static (HttpClient Client, RequestRecorder Recorder) Client(Row row)
    {
        var api = new StubApiHandler();
        api.On(new HttpMethod(row.Verb), "/" + row.Path.Replace("{id}", Id.ToString("D")),
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
