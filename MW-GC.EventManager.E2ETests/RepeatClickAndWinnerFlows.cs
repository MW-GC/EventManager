using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace MW_GC.EventManager.E2ETests;

/// <summary>
/// Browser flows for #140. Rapid repeat clicks on an Activity's Duplicate and on a confirmed Delete send one request
/// each, and Select Winner on an Event with two Selections saves and shows the Winner. Each flow adds its own Game,
/// Activities and Event through the API (behind the emulator, signed in as admin) and deletes them afterwards, so it
/// works on an empty Library as well as a seeded one.
/// </summary>
[TestClass]
public sealed class RepeatClickAndWinnerFlows
{
    // The first load downloads the WebAssembly runtime; give it room on a cold runner.
    private const float AppLoadTimeoutMs = 120_000;
    private const float UiTimeoutMs = 30_000;
    private const int RepeatClicks = 3;
    // Every counted request is held this long before it reaches the API, so all repeat clicks land while the first
    // request is still in flight: the window a busy guard has to cover.
    private static readonly TimeSpan HoldCountedRequest = TimeSpan.FromSeconds(4);
    // Time for a Delete question to render after a row's Delete is clicked, if the page lets it open.
    private const float QuestionRenderWaitMs = 400;
    // After the action has visibly finished, how long a late extra request still gets to show up.
    private const float StrayRequestWaitMs = 1_500;

    [TestMethod]
    public async Task RapidRepeatClicks_OnActivityDuplicate_SendOneRequest()
    {
        var appUrl = E2EEnvironment.AppUrlOrSkip();
        await using var session = await Session.StartAsync(appUrl);
        var tag = NewTag();
        var game = await session.PostAsync("/api/games", new { name = $"E2E Duplicate Game {tag}" });
        try
        {
            var activityName = $"E2E Duplicate {tag}";
            var activity = await session.PostAsync("/api/activities", new { gameId = Id(game), name = activityName, description = "d", rules = "r" });
            var page = session.Page;
            var requests = await CountAndHoldAsync(page, $"**/api/activities/{Id(activity)}/duplicate", "POST");

            await OpenAsync(page, appUrl + "/activities", "Activities");
            var duplicate = page.GetByRole(AriaRole.Button, new() { Name = $"Duplicate {activityName}", Exact = true });
            await Expect(duplicate).ToBeVisibleAsync(new() { Timeout = UiTimeoutMs });

            await ClickRapidlyAsync(page, await CentreOfAsync(duplicate), RepeatClicks);

            // Wait for a copy to show (one or more: the count below is the verdict), then for late extras.
            var copyName = $"{activityName} (Copy)";
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = $"Duplicate {copyName}", Exact = true }).First)
                .ToBeVisibleAsync(new() { Timeout = UiTimeoutMs });
            await page.WaitForTimeoutAsync(StrayRequestWaitMs);
            Assert.AreEqual(1, requests.Count, $"{RepeatClicks} rapid clicks on Duplicate sent {requests.Count} duplicate requests.");
            var copies = (await session.GetListAsync("/api/activities")).Count(a => Name(a) == copyName);
            Assert.AreEqual(1, copies, $"{RepeatClicks} rapid clicks on Duplicate made {copies} copies.");
        }
        finally
        {
            await session.DeleteGameAndActivitiesAsync(Id(game));
        }
    }

    [TestMethod]
    public async Task RapidRepeatClicks_OnConfirmedActivityDelete_SendOneRequest()
    {
        var appUrl = E2EEnvironment.AppUrlOrSkip();
        await using var session = await Session.StartAsync(appUrl);
        var tag = NewTag();
        var game = await session.PostAsync("/api/games", new { name = $"E2E Delete Game {tag}" });
        try
        {
            var activityName = $"E2E Delete {tag}";
            var activity = await session.PostAsync("/api/activities", new { gameId = Id(game), name = activityName, description = "d", rules = "r" });
            var page = session.Page;
            var requests = await CountAndHoldAsync(page, $"**/api/activities/{Id(activity)}", "DELETE");

            await OpenAsync(page, appUrl + "/activities", "Activities");
            var rowDelete = page.GetByRole(AriaRole.Button, new() { Name = $"Delete {activityName}", Exact = true });
            await Expect(rowDelete).ToBeVisibleAsync(new() { Timeout = UiTimeoutMs });
            var rowDeletePoint = await CentreOfAsync(rowDelete);
            await rowDelete.ClickAsync();
            var confirmDelete = page.GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true });
            await Expect(confirmDelete).ToBeVisibleAsync(new() { Timeout = UiTimeoutMs });
            var confirmPoint = await CentreOfAsync(confirmDelete);

            // While the first delete is in flight: hammer the question's Delete, then click the row's Delete again
            // and confirm any question it brings back, as an impatient admin would.
            await ClickRapidlyAsync(page, confirmPoint, RepeatClicks);
            for (var i = 0; i < RepeatClicks; i++)
            {
                await ClickRapidlyAsync(page, rowDeletePoint, 1);
                await page.WaitForTimeoutAsync(QuestionRenderWaitMs);
                if (await confirmDelete.CountAsync() > 0)
                    await ClickRapidlyAsync(page, await CentreOfAsync(confirmDelete), 1);
            }

            await Expect(rowDelete).ToHaveCountAsync(0, new() { Timeout = UiTimeoutMs });
            await page.WaitForTimeoutAsync(StrayRequestWaitMs);
            Assert.AreEqual(1, requests.Count, $"Rapid repeat clicks on Delete sent {requests.Count} delete requests.");
        }
        finally
        {
            await session.DeleteGameAndActivitiesAsync(Id(game));
        }
    }

    [TestMethod]
    public async Task SelectWinner_OnEventWithTwoSelections_SavesAndShowsTheWinner()
    {
        var appUrl = E2EEnvironment.AppUrlOrSkip();
        await using var session = await Session.StartAsync(appUrl);
        var tag = NewTag();
        var game = await session.PostAsync("/api/games", new { name = $"E2E Winner Game {tag}" });
        string? eventId = null;
        try
        {
            var first = await session.PostAsync("/api/activities", new { gameId = Id(game), name = $"E2E First {tag}", description = "d", rules = "r" });
            var secondName = $"E2E Second {tag}";
            var second = await session.PostAsync("/api/activities", new { gameId = Id(game), name = secondName, description = "d", rules = "r" });
            var eventName = $"E2E Winner Event {tag}";
            var created = await session.PostAsync("/api/events", new
            {
                name = eventName,
                date = "2026-10-01T00:00:00Z",
                uniqueGamesOnly = false,
                selections = new[] { new { game, activity = first }, new { game, activity = second } },
            });
            eventId = Id(created);
            Assert.IsNull(WinnerOf(created), "A new two-Selection Event must start without a Winner.");

            var page = session.Page;
            await OpenAsync(page, appUrl + "/events", "Events");
            var details = page.GetByRole(AriaRole.Button, new() { Name = $"Details {eventName}", Exact = true });
            await Expect(details).ToBeVisibleAsync(new() { Timeout = UiTimeoutMs });
            await details.ClickAsync();

            var selectWinner = page.GetByRole(AriaRole.Button, new() { Name = "Select Winner", Exact = true });
            await Expect(selectWinner).ToHaveCountAsync(2, new() { Timeout = UiTimeoutMs });
            // The Selections show in Event order, so the second button belongs to the second Selection.
            await selectWinner.Nth(1).ClickAsync();

            // The Winner's card swaps its button for the Winner badge; once the details close, the Events list
            // shows the Winner on the Event's row.
            await Expect(selectWinner).ToHaveCountAsync(1, new() { Timeout = UiTimeoutMs });
            await page.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).Last.ClickAsync();
            await Expect(page.GetByRole(AriaRole.Row).Filter(new() { HasText = eventName }))
                .ToContainTextAsync(secondName, new() { Timeout = UiTimeoutMs });
            var saved = await session.GetAsync($"/api/events/{eventId}");
            Assert.AreEqual(Id(second), WinnerOf(saved), "The saved Winner is not the chosen Selection.");
        }
        finally
        {
            if (eventId is not null) await session.DeleteAsync($"/api/events/{eventId}");
            await session.DeleteGameAndActivitiesAsync(Id(game));
        }
    }

    private static string NewTag() => Guid.NewGuid().ToString("N")[..8];

    private static string Id(JsonElement item) => item.GetProperty("id").GetString()!;

    private static string? Name(JsonElement item) => item.GetProperty("name").GetString();

    private static string? WinnerOf(JsonElement ev) =>
        ev.TryGetProperty("winnerActivityId", out var winner) && winner.ValueKind == JsonValueKind.String ? winner.GetString() : null;

    private static async Task OpenAsync(IPage page, string url, string heading)
    {
        await page.GotoAsync(url, new() { Timeout = AppLoadTimeoutMs });
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = heading, Level = 1, Exact = true }))
            .ToBeVisibleAsync(new() { Timeout = AppLoadTimeoutMs });
    }

    // Counts the requests with this method to this URL, holding each one before letting it through.
    private static async Task<RequestCounter> CountAndHoldAsync(IPage page, string urlGlob, string method)
    {
        var counter = new RequestCounter();
        await page.RouteAsync(urlGlob, async route =>
        {
            if (route.Request.Method == method)
            {
                counter.Add();
                await Task.Delay(HoldCountedRequest);
            }
            await route.ContinueAsync();
        });
        return counter;
    }

    private static async Task<(float X, float Y)> CentreOfAsync(ILocator target)
    {
        await target.ScrollIntoViewIfNeededAsync();
        var box = await target.BoundingBoxAsync() ?? throw new AssertFailedException("The button to click has no position on the page.");
        return (box.X + box.Width / 2, box.Y + box.Height / 2);
    }

    // Real mouse clicks at a fixed spot with no wait between them: no actionability check that would hold a click
    // until a disabled button is enabled again, so every click lands while the first action is still running.
    private static async Task ClickRapidlyAsync(IPage page, (float X, float Y) point, int times)
    {
        for (var i = 0; i < times; i++) await page.Mouse.ClickAsync(point.X, point.Y);
    }

    private sealed class RequestCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Add() => Interlocked.Increment(ref _count);
    }

    // One headless browser signed in as admin, and its API client, which shares the sign-in cookie.
    private sealed class Session : IAsyncDisposable
    {
        private readonly IPlaywright _playwright;
        private readonly IBrowser _browser;
        private readonly IBrowserContext _context;
        private readonly string _appUrl;

        private Session(IPlaywright playwright, IBrowser browser, IBrowserContext context, IPage page, string appUrl)
        {
            _playwright = playwright;
            _browser = browser;
            _context = context;
            Page = page;
            _appUrl = appUrl;
        }

        public IPage Page { get; }

        public static async Task<Session> StartAsync(string appUrl)
        {
            var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var context = await browser.NewContextAsync();
            await EmulatorSignIn.AsAdminAsync(context, appUrl);
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(UiTimeoutMs);
            return new Session(playwright, browser, context, page, appUrl);
        }

        public async Task<JsonElement> PostAsync(string path, object body)
        {
            var response = await _context.APIRequest.PostAsync(_appUrl + path, new() { DataObject = body });
            Assert.IsTrue(response.Ok, $"POST {path} answered {response.Status}: {await response.TextAsync()}");
            return (await response.JsonAsync())!.Value;
        }

        public async Task<JsonElement> GetAsync(string path)
        {
            var response = await _context.APIRequest.GetAsync(_appUrl + path);
            Assert.IsTrue(response.Ok, $"GET {path} answered {response.Status}: {await response.TextAsync()}");
            return (await response.JsonAsync())!.Value;
        }

        public async Task<List<JsonElement>> GetListAsync(string path) => [.. (await GetAsync(path)).EnumerateArray()];

        // Best effort: cleanup must not hide the flow's own result.
        public async Task DeleteAsync(string path)
        {
            try { await _context.APIRequest.DeleteAsync(_appUrl + path); }
            catch (PlaywrightException) { }
        }

        public async Task DeleteGameAndActivitiesAsync(string gameId)
        {
            try
            {
                foreach (var activity in await GetListAsync("/api/activities"))
                {
                    if (activity.GetProperty("gameId").GetString() == gameId) await DeleteAsync($"/api/activities/{Id(activity)}");
                }
            }
            catch (AssertFailedException) { }
            catch (PlaywrightException) { }
            await DeleteAsync($"/api/games/{gameId}");
        }

        public async ValueTask DisposeAsync()
        {
            await _context.CloseAsync();
            await _browser.CloseAsync();
            _playwright.Dispose();
        }
    }
}
