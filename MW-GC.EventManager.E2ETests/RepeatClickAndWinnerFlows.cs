using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;
using static MW_GC.EventManager.E2ETests.BrowserSession;

namespace MW_GC.EventManager.E2ETests;

/// <summary>
/// Browser flows for #140, #150 and #193. Rapid repeat clicks on an Activity's Duplicate and on a confirmed Delete
/// send one request each, and Select Winner on an Event with two Selections saves and shows the Winner. Each flow
/// adds its own Game, Activities and Event through the API (behind the emulator, signed in as admin) and deletes them
/// afterwards, so it works on an empty Library as well as a seeded one.
/// </summary>
/// <remarks>
/// The counted requests are held until the flow releases them, so every repeat click lands while the first request
/// is still in flight: the window a busy guard has to cover. Once they are released, the action has visibly finished
/// and the page has settled (it sent a request after the clicks, and none is in flight), every request the clicks
/// caused has been counted. No flow waits a fixed time.
/// </remarks>
[TestClass]
public sealed class RepeatClickAndWinnerFlows
{
    private const int RepeatClicks = 3;

    private static readonly Regex AnyActivityDuplicate = new(
        "/api/activities/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/duplicate$", RegexOptions.IgnoreCase);

    [TestMethod]
    public Task RapidRepeatClicks_OnActivityDuplicate_SendOneRequest() => RunAsync(async session =>
    {
        var tag = NewTag();
        var game = await session.Api.AddGameAsync($"E2E Duplicate Game {tag}");
        var activity = await session.Api.AddActivityAsync(game, $"E2E Duplicate {tag}");
        var requests = await session.Traffic.HoldAsync(AnyActivityDuplicate, HttpMethod.Post);

        await session.OpenListShowingAsync("activities", "Activities", activity.Name);
        var duplicate = session.Button($"Duplicate {activity.Name}");
        await Expect(duplicate).ToBeVisibleAsync(new() { Timeout = UiTimeoutMs });

        // One click action of several clicks with no delay between them: Playwright checks the button once, before
        // the first, so every later click lands while the first duplicate is still running.
        var mark = session.Traffic.Mark();
        await duplicate.ClickAsync(new() { ClickCount = RepeatClicks });

        // Let the duplicate through, wait for a copy to show (one or more: the count below is the verdict) and for
        // the page to settle.
        requests.Release();
        var copyName = $"{activity.Name} (Copy)";
        await Expect(session.Button($"Duplicate {copyName}").First).ToBeVisibleAsync(new() { Timeout = UiTimeoutMs });
        await session.Traffic.WaitForActionToSettleAsync(mark, "the Duplicate clicks");
        Assert.AreEqual(1, requests.Count, $"{RepeatClicks} rapid clicks on Duplicate sent {requests.Count} duplicate requests.");
        var copies = (await session.Api.ListActivitiesAsync()).Count(a => a.Name == copyName);
        Assert.AreEqual(1, copies, $"{RepeatClicks} rapid clicks on Duplicate made {copies} copies.");
    });

    [TestMethod]
    public Task RapidRepeatClicks_OnConfirmedActivityDelete_SendOneRequest() => RunAsync(async session =>
    {
        var activity = await AddActivityAsync(session, "Delete");
        await DeleteGuard.AssertHeldAsync(session, DeleteTarget.Activities, activity.Name,
            DeleteGuard.BurstAfterTheQuestionCloses(RepeatClicks),
            $"{RepeatClicks} rapid repeat clicks on an Activity's Delete while its delete was running");
    });

    // #193.1: the row's Delete clicked in the same browser task as Confirm, before the render that disables it.
    [TestMethod]
    public Task ClickBeforeTheBusyRender_OnConfirmedActivityDelete_SendsOneRequest() => RunAsync(async session =>
    {
        var activity = await AddActivityAsync(session, "Confirming Task");
        await DeleteGuard.AssertHeldAsync(session, DeleteTarget.Activities, activity.Name, DeleteGuard.InTheConfirmingTask(1),
            "A click on an Activity's Delete in the same browser task as its Confirm");
    });

    [TestMethod]
    public Task RapidSecondClick_OnConfirmedEventDelete_SendsOneRequest() => RunAsync(async session =>
    {
        var tag = NewTag();
        var game = await session.Api.AddGameAsync($"E2E Event Delete Game {tag}");
        var activity = await session.Api.AddActivityAsync(game, $"E2E Event Delete Activity {tag}");
        var created = await session.Api.AddEventAsync(EventBody.Of($"E2E Event Delete {tag}", true, new SelectionBody(game, activity)));

        await DeleteGuard.AssertHeldAsync(session, DeleteTarget.Events, created.Name, DeleteGuard.InTheConfirmingTask(1),
            "A second click on an Event's Delete in the same browser task as its Confirm");
    });

    // #193.6: the shared Library page's Delete guard on its other pages (Activities is above).
    [TestMethod]
    [DataRow("games", "Games", "game")]
    [DataRow("themes", "Themes", "theme")]
    [DataRow("holidays", "Holidays", "holiday")]
    public Task RapidRepeatClicks_OnConfirmedLibraryDelete_SendOneRequest(string route, string heading, string kind) => RunAsync(async session =>
    {
        var target = new DeleteTarget(route, heading, kind);
        var item = await session.Api.AddLibraryItemAsync(route, $"E2E Delete {heading} {NewTag()}");
        await DeleteGuard.AssertHeldAsync(session, target, item.Name, DeleteGuard.BurstAfterTheQuestionCloses(RepeatClicks),
            $"{RepeatClicks} rapid repeat clicks on a {kind}'s Delete while its delete was running");
    });

    [TestMethod]
    public Task SelectWinner_OnEventWithTwoSelections_SavesAndShowsTheWinner() => RunAsync(async session =>
    {
        var tag = NewTag();
        var game = await session.Api.AddGameAsync($"E2E Winner Game {tag}");
        var first = await session.Api.AddActivityAsync(game, $"E2E First {tag}");
        var second = await session.Api.AddActivityAsync(game, $"E2E Second {tag}");
        var created = await session.Api.AddEventAsync(EventBody.Of($"E2E Winner Event {tag}", false, new(game, first), new(game, second)));
        Assert.IsNull(created.WinnerActivityId, "A new two-Selection Event must start without a Winner.");

        var page = session.Page;
        await session.OpenListShowingAsync("events", "Events", created.Name);
        var details = session.Button($"Details {created.Name}");
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
        await Expect(page.GetByRole(AriaRole.Row).Filter(new() { HasText = created.Name }))
            .ToContainTextAsync(second.Name, new() { Timeout = UiTimeoutMs });
        var saved = await session.Api.GetEventAsync(created.Id);
        Assert.AreEqual(second.Id, saved.WinnerActivityId, "The saved Winner is not the chosen Selection.");
    });

    private static async Task<SeededActivity> AddActivityAsync(BrowserSession session, string what)
    {
        var tag = NewTag();
        var game = await session.Api.AddGameAsync($"E2E {what} Game {tag}");
        return await session.Api.AddActivityAsync(game, $"E2E {what} {tag}");
    }
}
