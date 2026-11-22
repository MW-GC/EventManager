using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;
using static MW_GC.EventManager.E2ETests.BrowserSession;

namespace MW_GC.EventManager.E2ETests;

/// <summary>
/// The Events page's two safety nets, checked in a real browser: Escape on a create dialog with unsaved
/// changes asks before it throws them away, and a delete names the Event and waits for a yes.
/// </summary>
/// <remarks>
/// A Fluent dialog's <c>role=dialog</c> element lives in its shadow root and shows the dialog's content
/// through a slot, so that content is not a DOM descendant of the dialog locator. The tests check the dialog
/// itself by role and name, and find what is inside it from the page, by names that are unique there.
/// </remarks>
[TestClass]
public sealed class DiscardAndDeleteFlows
{
    [TestMethod]
    public Task CreateEventDialog_TypedNameThenEscape_AsksToDiscard_KeepEditingKeepsTheText() => RunAsync(async session =>
    {
        var page = session.Page;
        await session.OpenEventsPageAsync();

        await session.Button("Create Event").ClickAsync();
        var dialog = Dialog(page, "Customize Event");
        await Expect(dialog).ToBeVisibleAsync();

        const string typed = "E2E unsaved name";
        var name = page.GetByRole(AriaRole.Textbox, new() { Name = "Event Name", Exact = true });
        await name.FillAsync(typed);
        await name.PressAsync("Escape");

        var discard = Dialog(page, "Discard changes?");
        await Expect(discard).ToBeVisibleAsync();
        await Expect(page.GetByText("Your changes in this dialog have not been saved.", new() { Exact = true }))
            .ToBeVisibleAsync();

        // Keep editing answers the question and leaves the dialog and the typed text alone.
        await session.Button("Keep editing").ClickAsync();
        await Expect(discard).ToBeHiddenAsync();
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(name).ToHaveValueAsync(typed);

        // Asked again, Discard closes the dialog without saving.
        await name.PressAsync("Escape");
        await Expect(discard).ToBeVisibleAsync();
        await session.Button("Discard").ClickAsync();
        await Expect(discard).ToBeHiddenAsync();
        await Expect(dialog).ToBeHiddenAsync();
    });

    [TestMethod]
    public Task DeleteEvent_ConfirmationNamesIt_CancelKeepsIt_ConfirmRemovesIt() => RunAsync(async session =>
    {
        // The library starts empty, so the test brings its own Game, Activity and Event; the session removes them
        // after (a 204 also covers the Event the flow deleted).
        var tag = Guid.NewGuid().ToString("N")[..8];
        var game = await session.AddGameAsync($"E2E Game {tag}");
        var activity = await session.PostAsync("/api/activities",
            new { gameId = Id(game), name = $"E2E Activity {tag}", description = "d", rules = "r" });
        var eventName = $"E2E Delete {tag}";
        var created = await session.AddEventAsync(new
        {
            name = eventName,
            date = DateTimeOffset.UtcNow,
            uniqueGamesOnly = true,
            selections = new[] { new { game, activity } },
        });
        var eventId = Id(created);

        var page = session.Page;
        await session.OpenEventsPageAsync();
        var rowDelete = session.Button($"Delete {eventName}");
        await Expect(rowDelete).ToBeVisibleAsync(new() { Timeout = AppLoadTimeoutMs });

        // The question names the Event; Cancel sends nothing and the Event stays.
        await rowDelete.ClickAsync();
        var confirm = Dialog(page, "Delete event");
        await Expect(confirm).ToBeVisibleAsync();
        await Expect(page.GetByText($"Delete {eventName}? This cannot be undone.", new() { Exact = true }))
            .ToBeVisibleAsync();
        await session.Button("Cancel").ClickAsync();
        await Expect(confirm).ToBeHiddenAsync();
        await Expect(rowDelete).ToBeVisibleAsync();
        Assert.AreEqual(200, await session.GetStatusAsync($"/api/events/{eventId}"), "Cancel must keep the Event.");

        // Confirm deletes it: the row goes from the list and the API no longer has it.
        await rowDelete.ClickAsync();
        await Expect(confirm).ToBeVisibleAsync();
        await session.Button("Delete").ClickAsync();
        await Expect(confirm).ToBeHiddenAsync();
        await Expect(rowDelete).ToHaveCountAsync(0);
        Assert.AreEqual(404, await session.GetStatusAsync($"/api/events/{eventId}"), "Confirm must delete the Event.");
    });

    // Every page dialog is a FluentDialog whose accessible name is its title.
    private static ILocator Dialog(IPage page, string name) =>
        page.GetByRole(AriaRole.Dialog, new() { Name = name, Exact = true });
}
