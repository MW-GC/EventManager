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
    private static readonly LocatorAssertionsToBeVisibleOptions Visible = new() { Timeout = UiTimeoutMs };
    private static readonly LocatorAssertionsToBeHiddenOptions Hidden = new() { Timeout = UiTimeoutMs };

    [TestMethod]
    public Task CreateEventDialog_TypedNameThenEscape_AsksToDiscard_KeepEditingKeepsTheText() => RunAsync(async session =>
    {
        var page = session.Page;
        await session.OpenEventsPageAsync();

        await session.Button("Create Event").ClickAsync();
        var dialog = session.Dialog("Customize Event");
        await Expect(dialog).ToBeVisibleAsync(Visible);

        const string typed = "E2E unsaved name";
        var name = page.GetByRole(AriaRole.Textbox, new() { Name = "Event Name", Exact = true });
        await name.FillAsync(typed);
        await name.PressAsync("Escape");

        var discard = session.Dialog("Discard changes?");
        await Expect(discard).ToBeVisibleAsync(Visible);
        await Expect(page.GetByText("Your changes in this dialog have not been saved.", new() { Exact = true }))
            .ToBeVisibleAsync(Visible);

        // Keep editing answers the question and leaves the dialog and the typed text alone.
        await session.Button("Keep editing").ClickAsync();
        await Expect(discard).ToBeHiddenAsync(Hidden);
        await Expect(dialog).ToBeVisibleAsync(Visible);
        await Expect(name).ToHaveValueAsync(typed, new() { Timeout = UiTimeoutMs });

        // Asked again, Discard closes the dialog without saving.
        await name.PressAsync("Escape");
        await Expect(discard).ToBeVisibleAsync(Visible);
        await session.Button("Discard").ClickAsync();
        await Expect(discard).ToBeHiddenAsync(Hidden);
        await Expect(dialog).ToBeHiddenAsync(Hidden);
    });

    [TestMethod]
    public Task DeleteEvent_ConfirmationNamesIt_CancelKeepsIt_ConfirmRemovesIt() => RunAsync(async session =>
    {
        // The library starts empty, so the test brings its own Game, Activity and Event; the session removes them
        // after (a 204 also covers the Event the flow deleted).
        var tag = NewTag();
        var game = await session.Api.AddGameAsync($"E2E Game {tag}");
        var activity = await session.Api.AddActivityAsync(game, $"E2E Activity {tag}");
        var created = await session.Api.AddEventAsync(EventBody.Of($"E2E Delete {tag}", true, new SelectionBody(game, activity)));
        var eventPath = $"/api/events/{created.Id}";

        var page = session.Page;
        await session.OpenListShowingAsync("events", "Events", created.Name);
        var rowDelete = session.Button($"Delete {created.Name}");
        await Expect(rowDelete).ToBeVisibleAsync(new() { Timeout = AppLoadTimeoutMs });

        // The question names the Event; Cancel sends nothing and the Event stays.
        await rowDelete.ClickAsync();
        var confirm = session.Dialog("Delete event");
        await Expect(confirm).ToBeVisibleAsync(Visible);
        await Expect(page.GetByText($"Delete {created.Name}? This cannot be undone.", new() { Exact = true }))
            .ToBeVisibleAsync(Visible);
        await session.Button("Cancel").ClickAsync();
        await Expect(confirm).ToBeHiddenAsync(Hidden);
        await Expect(rowDelete).ToBeVisibleAsync(Visible);
        Assert.AreEqual(200, await session.Api.GetStatusAsync(eventPath), "Cancel must keep the Event.");

        // Confirm deletes it: the row goes from the list and the API no longer has it.
        await rowDelete.ClickAsync();
        await Expect(confirm).ToBeVisibleAsync(Visible);
        await session.Button("Delete").ClickAsync();
        await Expect(confirm).ToBeHiddenAsync(Hidden);
        await Expect(rowDelete).ToHaveCountAsync(0, new() { Timeout = UiTimeoutMs });
        Assert.AreEqual(404, await session.Api.GetStatusAsync(eventPath), "Confirm must delete the Event.");
    });
}
