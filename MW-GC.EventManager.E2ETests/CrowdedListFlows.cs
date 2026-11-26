using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;
using static MW_GC.EventManager.E2ETests.BrowserSession;

namespace MW_GC.EventManager.E2ETests;

/// <summary>
/// #238: the flows find their own row on a stack that already holds data, as a live run leaves it (a Series makes
/// many Occurrences dated after the flows' Events). The Events list is a virtualized grid sorted newest first, so a
/// flow's row under a screenful of newer Events is not rendered; every flow opens its list through
/// <see cref="BrowserSession.OpenListShowingAsync"/>, which searches for the flow's own name.
/// </summary>
[TestClass]
public sealed class CrowdedListFlows
{
    // More than the grid renders in the browser's default viewport (24 rows in the #238 run).
    private const int NewerEvents = 40;

    [TestMethod]
    public Task OpenListShowing_EventUnderAScreenfulOfNewerEvents_ShowsItsRow() => RunAsync(async session =>
    {
        var tag = NewTag();
        var game = await session.Api.AddGameAsync($"E2E Crowded Game {tag}");
        var activity = await session.Api.AddActivityAsync(game, $"E2E Crowded Activity {tag}");
        var selection = new SelectionBody(game, activity);
        var target = await session.Api.AddEventAsync(EventBody.Of($"E2E Crowded Target {tag}", true, selection));
        var newest = target;
        for (var i = 1; i <= NewerEvents; i++)
        {
            var body = EventBody.Of($"E2E Crowd {tag} {i:00}", true, selection);
            newest = await session.Api.AddEventAsync(body with { Date = body.Date.AddDays(i) });
        }

        // Without the search the list shows its rows and the target is not among them: the list really is crowded.
        await session.OpenEventsPageAsync();
        var anyRow = session.Page.GetByRole(AriaRole.Row).Filter(new() { Has = session.Page.GetByRole(AriaRole.Gridcell) });
        await Expect(anyRow.First).ToBeVisibleAsync(new() { Timeout = AppLoadTimeoutMs });
        Assert.AreEqual(0, await session.Button($"Delete {target.Name}").CountAsync(),
            $"The target was rendered under {NewerEvents} newer Events, so this check cannot tell a crowded list from an empty one.");

        await session.OpenListShowingAsync("events", "Events", target.Name);
        await Expect(session.Button($"Delete {target.Name}")).ToBeVisibleAsync(new() { Timeout = UiTimeoutMs });
        await Expect(session.Button($"Delete {newest.Name}")).ToHaveCountAsync(0, new() { Timeout = UiTimeoutMs });
    });
}
