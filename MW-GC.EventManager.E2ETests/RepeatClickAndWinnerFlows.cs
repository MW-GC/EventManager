using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;
using static MW_GC.EventManager.E2ETests.BrowserSession;

namespace MW_GC.EventManager.E2ETests;

/// <summary>
/// Browser flows for #140 and #150. Rapid repeat clicks on an Activity's Duplicate, on an Activity's Delete and on an
/// Event's Delete send one request each, and Select Winner on an Event with two Selections saves and shows the
/// Winner. Each flow adds its own Game, Activities and Event through the API (behind the emulator, signed in as
/// admin) and deletes them afterwards, so it works on an empty Library as well as a seeded one.
/// </summary>
/// <remarks>
/// The counted requests are held until the flow releases them, so every repeat click lands while the first request
/// is still in flight: the window a busy guard has to cover. The page sends a request from the click that causes
/// it, so once the held requests are released, the action has visibly finished and the page has no request in
/// flight, every request the clicks caused has been counted. No flow waits a fixed time.
/// </remarks>
[TestClass]
public sealed class RepeatClickAndWinnerFlows
{
    private const int RepeatClicks = 3;
    private const string AnyId = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";

    // Every Activity's and every Event's own route, so a click that reaches another item's button counts too.
    private static readonly Regex AnyActivity = new($"/api/activities/{AnyId}$", RegexOptions.IgnoreCase);
    private static readonly Regex AnyActivityDuplicate = new($"/api/activities/{AnyId}/duplicate$", RegexOptions.IgnoreCase);
    private static readonly Regex AnyEvent = new($"/api/events/{AnyId}$", RegexOptions.IgnoreCase);

    [TestMethod]
    public Task RapidRepeatClicks_OnActivityDuplicate_SendOneRequest() => RunAsync(async session =>
    {
        var tag = NewTag();
        var game = await session.AddGameAsync($"E2E Duplicate Game {tag}");
        var activityName = $"E2E Duplicate {tag}";
        await session.PostAsync("/api/activities", new { gameId = Id(game), name = activityName, description = "d", rules = "r" });
        var page = session.Page;
        var requests = await session.HoldAsync(AnyActivityDuplicate, "POST");

        await session.OpenAsync("/activities", "Activities");
        var duplicate = session.Button($"Duplicate {activityName}");
        await Expect(duplicate).ToBeVisibleAsync();

        await ClickRapidlyAsync(page, await CentreOfAsync(duplicate), RepeatClicks);

        // Let the duplicate through, wait for a copy to show (one or more: the count below is the verdict) and for
        // the page to finish its requests.
        requests.Release();
        var copyName = $"{activityName} (Copy)";
        await Expect(session.Button($"Duplicate {copyName}").First).ToBeVisibleAsync();
        await session.WaitForNoRequestInFlightAsync();
        Assert.AreEqual(1, requests.Count, $"{RepeatClicks} rapid clicks on Duplicate sent {requests.Count} duplicate requests.");
        var copies = (await session.GetListAsync("/api/activities")).Count(a => Name(a) == copyName);
        Assert.AreEqual(1, copies, $"{RepeatClicks} rapid clicks on Duplicate made {copies} copies.");
    });

    [TestMethod]
    public Task RapidRepeatClicks_OnConfirmedActivityDelete_SendOneRequest() => RunAsync(async session =>
    {
        var tag = NewTag();
        var game = await session.AddGameAsync($"E2E Delete Game {tag}");
        var activityName = $"E2E Delete {tag}";
        await session.PostAsync("/api/activities", new { gameId = Id(game), name = activityName, description = "d", rules = "r" });
        var requests = await session.HoldAsync(AnyActivity, "DELETE");

        await session.OpenAsync("/activities", "Activities");
        const string title = "Delete activity";
        var questions = await CountQuestionsAsync(session.Page, title);
        var rowDelete = session.Button($"Delete {activityName}");
        var question = Dialog(session.Page, title);
        await rowDelete.ClickAsync();
        await Expect(question).ToBeVisibleAsync();
        await session.Button("Delete").ClickAsync();
        await Expect(question).ToBeHiddenAsync();

        // While the delete is held, a burst of clicks on the row's own Delete, found by its name each time. Force
        // skips the wait for an enabled button and nothing waits for a question to render, so the clicks land back
        // to back, as an impatient admin's would.
        var disabledWhileRunning = await rowDelete.IsDisabledAsync();
        for (var i = 0; i < RepeatClicks; i++) await rowDelete.ClickAsync(new() { Force = true });

        requests.Release();
        await Expect(rowDelete).ToHaveCountAsync(0);
        await ConfirmAnyQuestionLeftAsync(session, question);
        AssertGuardHeld(requests.Count, await questions.CountAsync(), disabledWhileRunning,
            $"{RepeatClicks} rapid repeat clicks on an Activity's Delete while its delete was running");
    });

    [TestMethod]
    public Task RapidSecondClick_OnConfirmedEventDelete_SendsOneRequest() => RunAsync(async session =>
    {
        var tag = NewTag();
        var game = await session.AddGameAsync($"E2E Event Delete Game {tag}");
        var activity = await session.PostAsync("/api/activities", new { gameId = Id(game), name = $"E2E Event Delete Activity {tag}", description = "d", rules = "r" });
        var eventName = $"E2E Event Delete {tag}";
        await session.AddEventAsync(new
        {
            name = eventName,
            date = "2026-10-01T00:00:00Z",
            uniqueGamesOnly = true,
            selections = new[] { new { game, activity } },
        });
        var requests = await session.HoldAsync(AnyEvent, "DELETE");

        await session.OpenEventsPageAsync();
        const string title = "Delete event";
        var questions = await CountQuestionsAsync(session.Page, title);
        var rowDelete = session.Button($"Delete {eventName}");
        var question = Dialog(session.Page, title);
        await rowDelete.ClickAsync();
        await Expect(question).ToBeVisibleAsync();
        await session.Button("Delete").ClickAsync();
        await Expect(question).ToBeHiddenAsync();

        // The page's own guard: the render that closes the question also disables every row's Delete while the
        // delete runs. A fast second click on it (Force skips Playwright's wait for an enabled button) must send
        // nothing and ask nothing.
        var disabledWhileRunning = await rowDelete.IsDisabledAsync();
        await rowDelete.ClickAsync(new() { Force = true });

        requests.Release();
        await Expect(rowDelete).ToHaveCountAsync(0);
        await ConfirmAnyQuestionLeftAsync(session, question);
        AssertGuardHeld(requests.Count, await questions.CountAsync(), disabledWhileRunning,
            "A fast second click on an Event's Delete while its delete was running");
    });

    [TestMethod]
    public Task SelectWinner_OnEventWithTwoSelections_SavesAndShowsTheWinner() => RunAsync(async session =>
    {
        var tag = NewTag();
        var game = await session.AddGameAsync($"E2E Winner Game {tag}");
        var first = await session.PostAsync("/api/activities", new { gameId = Id(game), name = $"E2E First {tag}", description = "d", rules = "r" });
        var secondName = $"E2E Second {tag}";
        var second = await session.PostAsync("/api/activities", new { gameId = Id(game), name = secondName, description = "d", rules = "r" });
        var eventName = $"E2E Winner Event {tag}";
        var created = await session.AddEventAsync(new
        {
            name = eventName,
            date = "2026-10-01T00:00:00Z",
            uniqueGamesOnly = false,
            selections = new[] { new { game, activity = first }, new { game, activity = second } },
        });
        var eventId = Id(created);
        Assert.IsNull(WinnerOf(created), "A new two-Selection Event must start without a Winner.");

        var page = session.Page;
        await session.OpenEventsPageAsync();
        var details = session.Button($"Details {eventName}");
        await Expect(details).ToBeVisibleAsync(new() { Timeout = UiTimeoutMs });
        await details.ClickAsync();

        var selectWinner = session.Button("Select Winner");
        await Expect(selectWinner).ToHaveCountAsync(2, new() { Timeout = UiTimeoutMs });
        // The Selections show in Event order, so the second button belongs to the second Selection.
        await selectWinner.Nth(1).ClickAsync();

        // The Winner's card swaps its button for the Winner badge; once the details close, the Events list
        // shows the Winner on the Event's row.
        await Expect(selectWinner).ToHaveCountAsync(1, new() { Timeout = UiTimeoutMs });
        await session.Button("Close").Last.ClickAsync();
        await Expect(page.GetByRole(AriaRole.Row).Filter(new() { HasText = eventName }))
            .ToContainTextAsync(secondName, new() { Timeout = UiTimeoutMs });
        var saved = await session.GetAsync($"/api/events/{eventId}");
        Assert.AreEqual(Id(second), WinnerOf(saved), "The saved Winner is not the chosen Selection.");
    });

    // One verdict for a Delete guard, so a failure names every part that gave way: the delete requests sent (one),
    // the delete questions asked (only the first), and whether the row's Delete was disabled while the delete ran.
    private static void AssertGuardHeld(int requests, int questions, bool disabled, string what) =>
        Assert.AreEqual("1 delete request, 1 question, Delete disabled",
            $"{requests} delete request{(requests == 1 ? "" : "s")}, {questions} question{(questions == 1 ? "" : "s")}, " +
            $"Delete {(disabled ? "disabled" : "enabled")}",
            $"{what}: the page's Delete guard did not hold.");

    // Once the page is idle: if a repeat click left a delete question showing, an impatient admin answers it, and the
    // request count then says whether the page let a second delete through.
    private static async Task ConfirmAnyQuestionLeftAsync(BrowserSession session, ILocator question)
    {
        await session.WaitForNoRequestInFlightAsync();
        if (!await question.IsVisibleAsync()) return;
        await session.Button("Delete").ClickAsync();
        await Expect(question).ToBeHiddenAsync();
        await session.WaitForNoRequestInFlightAsync();
    }

    // Counts, in the page, every question with this title that appears from now on. A click that slips past the
    // busy guard asks again; the next click may land on that question's overlay and answer Cancel, so the count is
    // the record of what was asked, whatever is still showing at the end.
    private static async Task<QuestionCount> CountQuestionsAsync(IPage page, string title)
    {
        var key = $"__e2eQuestions{Guid.NewGuid():N}";
        await page.EvaluateAsync(
            """
            ([key, title]) => {
                window[key] = 0;
                const seen = new WeakSet();
                const isQuestion = n => n.localName === 'fluent-dialog'
                    && (n.getAttribute('aria-label') === title || (n.textContent ?? '').includes(title));
                new MutationObserver(records => {
                    for (const record of records)
                        for (const node of record.addedNodes) {
                            if (node.nodeType !== 1) continue;
                            for (const dialog of [node, ...node.querySelectorAll('fluent-dialog')]) {
                                if (seen.has(dialog) || !isQuestion(dialog)) continue;
                                seen.add(dialog);
                                window[key]++;
                            }
                        }
                }).observe(document.body, { childList: true, subtree: true });
            }
            """, new[] { key, title });
        return new QuestionCount(page, key);
    }

    private sealed class QuestionCount(IPage page, string key)
    {
        public Task<int> CountAsync() => page.EvaluateAsync<int>("key => window[key]", key);
    }

    private static ILocator Dialog(IPage page, string name) =>
        page.GetByRole(AriaRole.Dialog, new() { Name = name, Exact = true });

    private static string NewTag() => Guid.NewGuid().ToString("N")[..8];

    private static string? Name(JsonElement item) => item.GetProperty("name").GetString();

    private static string? WinnerOf(JsonElement ev) =>
        ev.TryGetProperty("winnerActivityId", out var winner) && winner.ValueKind == JsonValueKind.String ? winner.GetString() : null;

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
}
