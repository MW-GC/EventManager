using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace MW_GC.EventManager.E2ETests;

/// <summary>
/// The page's own requests: which have started and not yet finished, and the routes a flow holds so its clicks land
/// while the first request is still in flight.
/// </summary>
internal sealed class PageTraffic
{
    private readonly TimeSpan _timeout;
    private readonly object _lock = new();
    private readonly HashSet<object> _inFlight = [];
    private int _started;
    private TaskCompletionSource _nextStart = New();
    private TaskCompletionSource _idle = New(done: true);
    private IPage? _page;
    private CleanupStack? _cleanups;

    /// <summary>Tracks nothing until <see cref="Started"/> and <see cref="Ended"/> report requests.</summary>
    internal PageTraffic(TimeSpan timeout) => _timeout = timeout;

    /// <summary>Tracks every request the page sends; holds are released by the session's cleanup.</summary>
    public static PageTraffic Watch(IPage page, CleanupStack cleanups, TimeSpan timeout)
    {
        var traffic = new PageTraffic(timeout) { _page = page, _cleanups = cleanups };
        page.Request += (_, request) => traffic.Started(request);
        page.RequestFinished += (_, request) => traffic.Ended(request);
        page.RequestFailed += (_, request) => traffic.Ended(request);
        return traffic;
    }

    /// <summary>How many requests the page has started so far. Take it before the action the flow waits on.</summary>
    public int Mark()
    {
        lock (_lock) return _started;
    }

    /// <summary>
    /// Waits until the page has started a request since <paramref name="mark"/>, then until none of its requests is
    /// in flight. Without the first wait, a request Playwright reports late would let the idle wait return before
    /// the action had sent anything. A request held by <see cref="HoldAsync"/> is in flight until it is released.
    /// </summary>
    public async Task WaitForActionToSettleAsync(int mark, string action)
    {
        Task started;
        lock (_lock) started = _started > mark ? Task.CompletedTask : _nextStart.Task;
        await WithinTimeoutAsync(started, () => $"The page sent no request for {action} within {_timeout.TotalMilliseconds} ms.");

        Task idle;
        lock (_lock) idle = _idle.Task;
        await WithinTimeoutAsync(idle, () =>
        {
            string open;
            lock (_lock) open = string.Join(", ", _inFlight.OfType<IRequest>().Select(r => $"{r.Method} {r.Url}"));
            return $"After {action} the page still had requests in flight after {_timeout.TotalMilliseconds} ms: {open}";
        });
    }

    /// <summary>
    /// Counts the page's requests with this method to a URL that matches, and holds each one until
    /// <see cref="HeldRequests.Release"/>, so every click made meanwhile lands while the first request is still in
    /// flight: the window a busy guard has to cover. Cleanup releases them in case the flow stopped first.
    /// </summary>
    public async Task<HeldRequests> HoldAsync(Regex url, HttpMethod method)
    {
        var page = _page ?? throw new InvalidOperationException("This traffic watches no page.");
        var held = new HeldRequests(method);
        await page.RouteAsync(url, held.HandleAsync);
        _cleanups!.Push($"held {method} {url}", () => { held.Release(); return Task.CompletedTask; });
        return held;
    }

    internal void Started(object request)
    {
        lock (_lock)
        {
            _started++;
            _nextStart.TrySetResult();
            _nextStart = New();
            if (_inFlight.Add(request) && _inFlight.Count == 1) _idle = New();
        }
    }

    internal void Ended(object request)
    {
        lock (_lock)
        {
            if (_inFlight.Remove(request) && _inFlight.Count == 0) _idle.TrySetResult();
        }
    }

    private async Task WithinTimeoutAsync(Task task, Func<string> failure)
    {
        try
        {
            await task.WaitAsync(_timeout);
        }
        catch (TimeoutException)
        {
            throw new AssertFailedException(failure());
        }
    }

    private static TaskCompletionSource New(bool done = false)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (done) source.SetResult();
        return source;
    }
}

/// <summary>The requests a <see cref="PageTraffic.HoldAsync"/> route counted, held until released.</summary>
internal sealed class HeldRequests(HttpMethod method)
{
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _count;

    /// <summary>How many matching requests the page has sent so far.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Lets every held request, and every later one, through to the API.</summary>
    public void Release() => _released.TrySetResult();

    internal async Task HandleAsync(IRoute route)
    {
        if (route.Request.Method == method.Method)
        {
            Interlocked.Increment(ref _count);
            await _released.Task;
        }
        await route.ContinueAsync();
    }
}
