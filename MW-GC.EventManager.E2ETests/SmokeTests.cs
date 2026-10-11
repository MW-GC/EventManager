using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace MW_GC.EventManager.E2ETests;

[TestClass]
public sealed class SmokeTests
{
    // The first load downloads the WebAssembly runtime; give it room on a cold runner.
    private const float AppLoadTimeoutMs = 120_000;

    [TestMethod]
    public async Task SignedInAdmin_OpensEventsPage_SeesItsHeading()
    {
        var appUrl = E2EEnvironment.AppUrlOrSkip();

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        await EmulatorSignIn.AsAdminAsync(context, appUrl);
        var page = await context.NewPageAsync();

        await page.GotoAsync(appUrl + "/events", new() { Timeout = AppLoadTimeoutMs });

        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Events", Level = 1, Exact = true }))
            .ToBeVisibleAsync(new() { Timeout = AppLoadTimeoutMs });
    }
}
