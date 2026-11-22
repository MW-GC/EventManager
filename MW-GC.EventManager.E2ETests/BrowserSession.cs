using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace MW_GC.EventManager.E2ETests;

/// <summary>
/// One headless Chromium signed in to the emulator as admin, and its page. Every browser flow runs through
/// <see cref="RunAsync"/>, which starts the browser, runs the flow, then runs the cleanups (newest first, so an Event
/// goes before its Game and Activities, and the browser closes last). The flow seeds data through <see cref="Api"/>
/// and watches and holds the page's requests through <see cref="Traffic"/>. Flows read this class through
/// <c>using static</c>: <see cref="RunAsync"/>, <see cref="NewTag"/> and the timeouts.
/// </summary>
/// <remarks>
/// Cleanup is never silent and every step runs even when an earlier one failed; <see cref="FlowOutcome"/> reports a
/// failed cleanup without hiding the flow's own result.
/// </remarks>
internal sealed class BrowserSession
{
    // The first load downloads the WebAssembly runtime; give it room on a cold runner.
    public const float AppLoadTimeoutMs = 120_000;

    /// <summary>
    /// Every other UI wait: Playwright actions (through <see cref="IPage.SetDefaultTimeout"/>) and every
    /// <c>Expect</c>, which takes it explicitly because <see cref="IPage.SetDefaultTimeout"/> does not cover
    /// assertions (their own default is 5 s, too short for a cold CI machine).
    /// </summary>
    public const float UiTimeoutMs = 30_000;

    private readonly CleanupStack _cleanups;
    private readonly string _appUrl;

    private BrowserSession(IBrowserContext context, IPage page, string appUrl, CleanupStack cleanups)
    {
        _cleanups = cleanups;
        _appUrl = appUrl;
        Page = page;
        Api = new ApiSeedClient(context.APIRequest, appUrl, cleanups);
        Traffic = PageTraffic.Watch(page, cleanups, TimeSpan.FromMilliseconds(UiTimeoutMs));
    }

    public IPage Page { get; }

    public ApiSeedClient Api { get; }

    public PageTraffic Traffic { get; }

    /// <summary>
    /// Runs one flow in a fresh signed-in browser. The test is inconclusive (or fails, when <c>EM_E2E_REQUIRED</c> is
    /// <c>1</c>) without <c>EM_E2E_URL</c>.
    /// </summary>
    public static async Task RunAsync(Func<BrowserSession, Task> flow)
    {
        var session = await StartAsync(E2EEnvironment.AppUrlOrSkip());

        Exception? flowFailure = null;
        try
        {
            await flow(session);
        }
        catch (Exception ex)
        {
            flowFailure = ex;
        }

        FlowOutcome.Report(flowFailure, await session._cleanups.RunAllAsync());
    }

    /// <summary>
    /// Starts Playwright and the browser and signs in. If a later step throws, what already started is closed again
    /// before the failure goes up, so a failed start leaks no browser.
    /// </summary>
    internal static async Task<BrowserSession> StartAsync(string appUrl, Func<IBrowserContext, string, Task>? signIn = null)
    {
        var cleanups = new CleanupStack();
        try
        {
            var playwright = await Playwright.CreateAsync();
            cleanups.Push("Playwright", () => { playwright.Dispose(); return Task.CompletedTask; });
            var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            cleanups.Push("the browser", () => browser.CloseAsync());
            var context = await browser.NewContextAsync();
            await (signIn ?? EmulatorSignIn.AsAdminAsync)(context, appUrl);
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(UiTimeoutMs);
            return new BrowserSession(context, page, appUrl, cleanups);
        }
        catch (Exception ex)
        {
            FlowOutcome.Report(ex, await cleanups.RunAllAsync());
            throw;
        }
    }

    /// <summary>Opens a page of the app and waits until its level-one heading shows.</summary>
    public async Task OpenAsync(string path, string heading)
    {
        await Page.GotoAsync(_appUrl + path, new() { Timeout = AppLoadTimeoutMs });
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = heading, Level = 1, Exact = true }))
            .ToBeVisibleAsync(new() { Timeout = AppLoadTimeoutMs });
    }

    public Task OpenEventsPageAsync() => OpenAsync("/events", "Events");

    /// <summary>A button by its exact accessible name.</summary>
    public ILocator Button(string name) => Page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });

    /// <summary>A dialog by its exact accessible name: every page dialog is a FluentDialog named by its title.</summary>
    public ILocator Dialog(string name) => Page.GetByRole(AriaRole.Dialog, new() { Name = name, Exact = true });

    /// <summary>A short unique tag for the names of the items a flow adds.</summary>
    public static string NewTag() => Guid.NewGuid().ToString("N")[..8];
}
