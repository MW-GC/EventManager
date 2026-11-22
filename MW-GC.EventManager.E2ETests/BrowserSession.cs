using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace MW_GC.EventManager.E2ETests;

/// <summary>
/// One headless Chromium signed in to the emulator as admin, its page, and an API client that shares the sign-in
/// cookie. Every browser flow runs through <see cref="RunAsync"/>, which starts the browser, runs the flow, then runs
/// the cleanups the flow registered (newest first, so an Event goes before its Game and Activities) and closes the
/// browser.
/// </summary>
/// <remarks>
/// Cleanup is never silent: every delete must answer with a success status, and every cleanup step runs even when
/// an earlier one failed. A failed cleanup fails the test; when the flow failed as well, both failures are reported
/// together, so cleanup neither hides the flow's result nor disappears behind it.
/// </remarks>
internal sealed class BrowserSession : IAsyncDisposable
{
    // The first load downloads the WebAssembly runtime; give it room on a cold runner.
    public const float AppLoadTimeoutMs = 120_000;
    public const float UiTimeoutMs = 30_000;

    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;
    private readonly IBrowserContext _context;
    private readonly Stack<(string What, Func<Task> Step)> _cleanups = new();

    // The page's own requests that have started and not yet finished or failed.
    private readonly object _traffic = new();
    private readonly HashSet<IRequest> _inFlight = [];
    private TaskCompletionSource _idle = NewIdle(done: true);

    private BrowserSession(IPlaywright playwright, IBrowser browser, IBrowserContext context, IPage page, string appUrl)
    {
        _playwright = playwright;
        _browser = browser;
        _context = context;
        Page = page;
        AppUrl = appUrl;
        page.Request += (_, request) => Started(request);
        page.RequestFinished += (_, request) => Ended(request);
        page.RequestFailed += (_, request) => Ended(request);
    }

    /// <summary>The app's base URL without a trailing slash.</summary>
    public string AppUrl { get; }

    public IPage Page { get; }

    /// <summary>
    /// Runs one flow in a fresh signed-in browser. The test is inconclusive (or fails, when <c>EM_E2E_REQUIRED</c> is
    /// <c>1</c>) without <c>EM_E2E_URL</c>.
    /// </summary>
    public static async Task RunAsync(Func<BrowserSession, Task> flow)
    {
        var appUrl = E2EEnvironment.AppUrlOrSkip();
        await using var session = await StartAsync(appUrl);

        Exception? flowFailure = null;
        try
        {
            await flow(session);
        }
        catch (Exception ex)
        {
            flowFailure = ex;
        }

        var cleanupFailures = await session.CleanUpAsync();
        if (flowFailure is null && cleanupFailures.Count == 0) return;
        if (cleanupFailures.Count == 0) ExceptionDispatchInfo.Throw(flowFailure!);
        if (flowFailure is null)
            throw new AssertFailedException($"Cleanup failed: {string.Join(" | ", cleanupFailures.Select(f => f.Message))}",
                new AggregateException(cleanupFailures));
        throw new AggregateException("The flow failed, and its cleanup failed too.", [flowFailure, .. cleanupFailures]);
    }

    private static async Task<BrowserSession> StartAsync(string appUrl)
    {
        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var context = await browser.NewContextAsync();
        await EmulatorSignIn.AsAdminAsync(context, appUrl);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(UiTimeoutMs);
        return new BrowserSession(playwright, browser, context, page, appUrl);
    }

    /// <summary>Opens a page of the app and waits until its level-one heading shows.</summary>
    public async Task OpenAsync(string path, string heading)
    {
        await Page.GotoAsync(AppUrl + path, new() { Timeout = AppLoadTimeoutMs });
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = heading, Level = 1, Exact = true }))
            .ToBeVisibleAsync(new() { Timeout = AppLoadTimeoutMs });
    }

    public Task OpenEventsPageAsync() => OpenAsync("/events", "Events");

    /// <summary>A button by its exact accessible name.</summary>
    public ILocator Button(string name) => Page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });

    /// <summary>
    /// Waits until none of the page's requests is in flight: every request it started has finished or failed.
    /// A request held by <see cref="HoldAsync"/> counts as in flight until it is released and answered.
    /// </summary>
    public async Task WaitForNoRequestInFlightAsync()
    {
        Task idle;
        lock (_traffic) idle = _idle.Task;
        try
        {
            await idle.WaitAsync(TimeSpan.FromMilliseconds(UiTimeoutMs));
        }
        catch (TimeoutException)
        {
            string open;
            lock (_traffic) open = string.Join(", ", _inFlight.Select(r => $"{r.Method} {r.Url}"));
            throw new AssertFailedException($"The page still had requests in flight after {UiTimeoutMs} ms: {open}");
        }
    }

    /// <summary>
    /// Counts the page's requests with this method to a URL that matches, and holds each one until
    /// <see cref="HeldRequests.Release"/>, so every click made meanwhile lands while the first request is still in
    /// flight: the window a busy guard has to cover. Cleanup releases them in case the flow stopped first.
    /// </summary>
    public async Task<HeldRequests> HoldAsync(Regex url, string method)
    {
        var held = new HeldRequests(method);
        await Page.RouteAsync(url, held.HandleAsync);
        OnCleanup($"held {method} {url}", () => { held.Release(); return Task.CompletedTask; });
        return held;
    }

    /// <summary>Adds a Game through the API; cleanup deletes its Activities, then the Game.</summary>
    public async Task<JsonElement> AddGameAsync(string name)
    {
        var game = await PostAsync("/api/games", new { name });
        var gameId = Id(game);
        OnCleanup($"Game {name}", () => DeleteGameAndActivitiesAsync(gameId));
        return game;
    }

    /// <summary>Adds an Event through the API; cleanup deletes it (a 204 also covers an Event the flow deleted).</summary>
    public async Task<JsonElement> AddEventAsync(object body)
    {
        var created = await PostAsync("/api/events", body);
        var eventId = Id(created);
        OnCleanup($"Event {eventId}", () => DeleteAsync($"/api/events/{eventId}"));
        return created;
    }

    /// <summary>Registers a cleanup step; steps run newest first after the flow, whether it passed or not.</summary>
    public void OnCleanup(string what, Func<Task> step) => _cleanups.Push((what, step));

    /// <summary>Calls the API through the emulator with the admin cookie and returns the saved item.</summary>
    public async Task<JsonElement> PostAsync(string path, object body)
    {
        var response = await _context.APIRequest.PostAsync(AppUrl + path, new() { DataObject = body });
        Assert.IsTrue(response.Ok, $"POST {path} answered {response.Status}: {await response.TextAsync()}");
        return (await response.JsonAsync())!.Value;
    }

    public async Task<JsonElement> GetAsync(string path)
    {
        var response = await _context.APIRequest.GetAsync(AppUrl + path);
        Assert.IsTrue(response.Ok, $"GET {path} answered {response.Status}: {await response.TextAsync()}");
        return (await response.JsonAsync())!.Value;
    }

    public async Task<List<JsonElement>> GetListAsync(string path) => [.. (await GetAsync(path)).EnumerateArray()];

    /// <summary>The status a GET of this path answers, for flows that check whether an item still exists.</summary>
    public async Task<int> GetStatusAsync(string path) => (await _context.APIRequest.GetAsync(AppUrl + path)).Status;

    /// <summary>Deletes through the API and fails unless the API answers with a success status.</summary>
    public async Task DeleteAsync(string path)
    {
        var response = await _context.APIRequest.DeleteAsync(AppUrl + path);
        Assert.IsTrue(response.Ok, $"DELETE {path} answered {response.Status}: {await response.TextAsync()}");
    }

    public static string Id(JsonElement item) => item.GetProperty("id").GetString()!;

    // Children first: a Game that still has Activities refuses its delete.
    private async Task DeleteGameAndActivitiesAsync(string gameId)
    {
        foreach (var activity in await GetListAsync("/api/activities"))
        {
            if (activity.GetProperty("gameId").GetString() == gameId) await DeleteAsync($"/api/activities/{Id(activity)}");
        }
        await DeleteAsync($"/api/games/{gameId}");
    }

    // Runs every step even when one fails, and returns what failed.
    private async Task<List<Exception>> CleanUpAsync()
    {
        var failures = new List<Exception>();
        while (_cleanups.TryPop(out var cleanup))
        {
            try
            {
                await cleanup.Step();
            }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException($"Cleanup of {cleanup.What} failed: {ex.Message}", ex));
            }
        }
        return failures;
    }

    private void Started(IRequest request)
    {
        lock (_traffic)
        {
            if (_inFlight.Add(request) && _inFlight.Count == 1) _idle = NewIdle(done: false);
        }
    }

    private void Ended(IRequest request)
    {
        lock (_traffic)
        {
            if (_inFlight.Remove(request) && _inFlight.Count == 0) _idle.TrySetResult();
        }
    }

    private static TaskCompletionSource NewIdle(bool done)
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (done) idle.SetResult();
        return idle;
    }

    public async ValueTask DisposeAsync()
    {
        await _context.CloseAsync();
        await _browser.CloseAsync();
        _playwright.Dispose();
    }
}

/// <summary>The requests a <see cref="BrowserSession.HoldAsync"/> route counted, held until released.</summary>
internal sealed class HeldRequests(string method)
{
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _count;

    /// <summary>How many matching requests the page has sent so far.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Lets every held request, and every later one, through to the API.</summary>
    public void Release() => _released.TrySetResult();

    internal async Task HandleAsync(IRoute route)
    {
        if (route.Request.Method == method)
        {
            Interlocked.Increment(ref _count);
            await _released.Task;
        }
        await route.ContinueAsync();
    }
}
