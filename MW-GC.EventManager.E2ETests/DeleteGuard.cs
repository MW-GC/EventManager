using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;
using static MW_GC.EventManager.E2ETests.BrowserSession;

namespace MW_GC.EventManager.E2ETests;

/// <summary>A page whose rows have a confirmed Delete: where it lives and how its delete question is titled.</summary>
/// <param name="Route">The API route of its items and the page's path: <c>activities</c>, <c>events</c>, ...</param>
/// <param name="Heading">The page's level-one heading.</param>
/// <param name="Kind">One item in lower case, as the question title says it: <c>activity</c>, <c>event</c>, ...</param>
internal sealed record DeleteTarget(string Route, string Heading, string Kind)
{
    private const string AnyId = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";

    public static readonly DeleteTarget Activities = new("activities", "Activities", "activity");
    public static readonly DeleteTarget Events = new("events", "Events", "event");

    public string Question => $"Delete {Kind}";

    /// <summary>Every item's own route, so a click that reaches another row's Delete counts too.</summary>
    public Regex AnyItem => new($"/api/{Route}/{AnyId}$", RegexOptions.IgnoreCase);
}

/// <summary>
/// Clicks on a row's Delete while the delete it confirmed is still running, and the verdict on the page's guard.
/// The delete requests are held until the clicks are done, so every click lands while the first delete is in
/// flight: the window the guard has to cover.
/// </summary>
internal static class DeleteGuard
{
    /// <summary>The clicks a flow makes once the question is showing; the guard must turn all but the first away.</summary>
    public delegate Task Clicks(BrowserSession session, ILocator rowDelete, ILocator question);

    /// <summary>
    /// Confirms, waits for the question to close, then a burst of forced clicks on the row's Delete, found by its
    /// name each time. Force skips the wait for an enabled button and nothing waits for a question to render, so the
    /// clicks land back to back, as an impatient admin's would.
    /// </summary>
    public static Clicks BurstAfterTheQuestionCloses(int times) => async (session, rowDelete, question) =>
    {
        await session.Button("Delete").ClickAsync();
        await Expect(question).ToBeHiddenAsync(new() { Timeout = UiTimeoutMs });
        for (var i = 0; i < times; i++) await rowDelete.ClickAsync(new() { Force = true });
    };

    /// <summary>
    /// Confirm and the row's Delete in one browser task: the row clicks are issued from inside the page's own fetch
    /// of the DELETE, while the app is still handling Confirm and before the render that disables the row's Delete.
    /// The flow checks that the row was still enabled when it clicked, so it fails rather than pass without
    /// reaching that window.
    /// </summary>
    public static Clicks InTheConfirmingTask(int times) => async (session, rowDelete, _) =>
    {
        var rowName = await rowDelete.GetAttributeAsync("aria-label")
            ?? throw new AssertFailedException("The row's Delete has no accessible name to find it by.");
        var clickedWhileEnabled = await session.Button("Delete").EvaluateAsync<int>(
            """
            (confirm, [rowName, times]) => {
                const row = () => [...document.querySelectorAll('[aria-label]')].find(e => e.getAttribute('aria-label') === rowName);
                let enabledClicks = -1;
                const fetch = window.fetch;
                window.fetch = function (input, init) {
                    const method = (init?.method ?? input?.method ?? 'GET').toUpperCase();
                    if (method === 'DELETE' && enabledClicks < 0) {
                        window.fetch = fetch;
                        enabledClicks = 0;
                        for (let i = 0; i < times; i++) {
                            const target = row();
                            if (target && !target.hasAttribute('disabled')) enabledClicks++;
                            target?.click();
                        }
                    }
                    return fetch.apply(this, arguments);
                };
                confirm.click();
                window.fetch = fetch;
                return enabledClicks;
            }
            """, new object[] { rowName, times });
        Assert.AreEqual(times, clickedWhileEnabled,
            "The row's Delete clicks did not all land before the render that disables it (-1: Confirm sent no DELETE).");
    };

    /// <summary>
    /// Opens the page, asks to delete the item, makes the clicks, and checks the guard: the row's Delete is disabled
    /// while the delete runs, and in the end one delete request was sent and one question asked.
    /// </summary>
    public static async Task AssertHeldAsync(BrowserSession session, DeleteTarget target, string itemName, Clicks clicks, string what)
    {
        var requests = await session.Traffic.HoldAsync(target.AnyItem, HttpMethod.Delete);
        await session.OpenAsync($"/{target.Route}", target.Heading);
        var questions = await QuestionCount.StartAsync(session.Page, target.Question);
        var question = session.Dialog(target.Question);
        var rowDelete = session.Button($"Delete {itemName}");
        var mark = session.Traffic.Mark();

        await rowDelete.ClickAsync();
        await Expect(question).ToBeVisibleAsync(new() { Timeout = UiTimeoutMs });
        await clicks(session, rowDelete, question);
        var disabledWhileRunning = await IsDisabledWithinTimeoutAsync(rowDelete);

        requests.Release();
        await Expect(rowDelete).ToHaveCountAsync(0, new() { Timeout = UiTimeoutMs });
        await ConfirmAnyQuestionLeftAsync(session, question, mark, what);
        var asked = await questions.CountAsync();
        // One verdict, so a failure names every part that gave way.
        Assert.AreEqual("1 delete request, 1 question, Delete disabled",
            $"{requests.Count} delete request{(requests.Count == 1 ? "" : "s")}, {asked} question{(asked == 1 ? "" : "s")}, " +
            $"Delete {(disabledWhileRunning ? "disabled" : "enabled")}",
            $"{what}: the page's Delete guard did not hold.");
    }

    // The retrying assertion, read as a value so the verdict can name it with the counts.
    private static async Task<bool> IsDisabledWithinTimeoutAsync(ILocator button)
    {
        try
        {
            await Expect(button).ToBeDisabledAsync(new() { Timeout = UiTimeoutMs });
            return true;
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    // Once the page is idle: if a repeat click left a delete question showing, an impatient admin answers it, and the
    // request count then says whether the page let a second delete through.
    private static async Task ConfirmAnyQuestionLeftAsync(BrowserSession session, ILocator question, int mark, string what)
    {
        await session.Traffic.WaitForActionToSettleAsync(mark, what);
        if (!await question.IsVisibleAsync()) return;
        var answer = session.Traffic.Mark();
        await session.Button("Delete").ClickAsync();
        await Expect(question).ToBeHiddenAsync(new() { Timeout = UiTimeoutMs });
        await session.Traffic.WaitForActionToSettleAsync(answer, "the answer to a delete question left showing");
    }

    // Counts, in the page, every dialog with this title that appears from now on, matched by its role: a Fluent
    // dialog's role=dialog element sits in its shadow root, named by its own aria-label or its host's. No tag-name
    // fallback: the role match counted every question in the live runs. A click that slips past
    // the busy guard asks again; the next click may land on that question's overlay and answer Cancel, so the count is
    // the record of what was asked, whatever is still showing at the end.
    private sealed class QuestionCount(IPage page, string key)
    {
        public static async Task<QuestionCount> StartAsync(IPage page, string title)
        {
            var key = $"__e2eQuestions{Guid.NewGuid():N}";
            await page.EvaluateAsync(
                """
                ([key, title]) => {
                    window[key] = 0;
                    const seen = new WeakSet();
                    const dialogsIn = node => [node, ...node.querySelectorAll('*')]
                        .flatMap(e => [e, ...(e.shadowRoot?.querySelectorAll('[role="dialog"]') ?? [])])
                        .filter(e => e.getAttribute('role') === 'dialog');
                    const nameOf = dialog => dialog.getAttribute('aria-label') ?? dialog.getRootNode().host?.getAttribute('aria-label');
                    new MutationObserver(records => {
                        for (const record of records)
                            for (const node of record.addedNodes) {
                                if (node.nodeType !== 1) continue;
                                for (const dialog of dialogsIn(node)) {
                                    if (seen.has(dialog) || nameOf(dialog) !== title) continue;
                                    seen.add(dialog);
                                    window[key]++;
                                }
                            }
                    }).observe(document.body, { childList: true, subtree: true });
                }
                """, new[] { key, title });
            return new QuestionCount(page, key);
        }

        public Task<int> CountAsync() => page.EvaluateAsync<int>("key => window[key]", key);
    }
}
