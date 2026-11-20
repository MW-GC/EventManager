using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

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
    // The first load downloads the WebAssembly runtime; give it room on a cold runner.
    private const float AppLoadTimeoutMs = 120_000;

    [TestMethod]
    public async Task CreateEventDialog_TypedNameThenEscape_AsksToDiscard_KeepEditingKeepsTheText()
    {
        var appUrl = E2EEnvironment.AppUrlOrSkip();

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        await EmulatorSignIn.AsAdminAsync(context, appUrl);
        var page = await context.NewPageAsync();
        await OpenEventsPageAsync(page, appUrl);

        await Button(page, "Create Event").ClickAsync();
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
        await Button(page, "Keep editing").ClickAsync();
        await Expect(discard).ToBeHiddenAsync();
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(name).ToHaveValueAsync(typed);

        // Asked again, Discard closes the dialog without saving.
        await name.PressAsync("Escape");
        await Expect(discard).ToBeVisibleAsync();
        await Button(page, "Discard").ClickAsync();
        await Expect(discard).ToBeHiddenAsync();
        await Expect(dialog).ToBeHiddenAsync();
    }

    [TestMethod]
    public async Task DeleteEvent_ConfirmationNamesIt_CancelKeepsIt_ConfirmRemovesIt()
    {
        var appUrl = E2EEnvironment.AppUrlOrSkip();

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        await EmulatorSignIn.AsAdminAsync(context, appUrl);
        var api = context.APIRequest;

        // The library starts empty, so the test brings its own Game, Activity and Event and removes them after.
        var tag = Guid.NewGuid().ToString("N")[..8];
        string? gameId = null, activityId = null, eventId = null;
        try
        {
            var game = await PostAsync(api, appUrl, "/api/games", new { name = $"E2E Game {tag}" });
            gameId = game.GetProperty("id").GetString();
            var activity = await PostAsync(api, appUrl, "/api/activities",
                new { gameId, name = $"E2E Activity {tag}", description = "d", rules = "r" });
            activityId = activity.GetProperty("id").GetString();
            var eventName = $"E2E Delete {tag}";
            var created = await PostAsync(api, appUrl, "/api/events", new
            {
                name = eventName,
                date = DateTimeOffset.UtcNow,
                uniqueGamesOnly = true,
                selections = new[] { new { game, activity } },
            });
            eventId = created.GetProperty("id").GetString();

            var page = await context.NewPageAsync();
            await OpenEventsPageAsync(page, appUrl);
            var rowDelete = Button(page, $"Delete {eventName}");
            await Expect(rowDelete).ToBeVisibleAsync(new() { Timeout = AppLoadTimeoutMs });

            // The question names the Event; Cancel sends nothing and the Event stays.
            await rowDelete.ClickAsync();
            var confirm = Dialog(page, "Delete event");
            await Expect(confirm).ToBeVisibleAsync();
            await Expect(page.GetByText($"Delete {eventName}? This cannot be undone.", new() { Exact = true }))
                .ToBeVisibleAsync();
            await Button(page, "Cancel").ClickAsync();
            await Expect(confirm).ToBeHiddenAsync();
            await Expect(rowDelete).ToBeVisibleAsync();
            Assert.AreEqual(200, (await api.GetAsync($"{appUrl}/api/events/{eventId}")).Status,
                "Cancel must keep the Event.");

            // Confirm deletes it: the row goes from the list and the API no longer has it.
            await rowDelete.ClickAsync();
            await Expect(confirm).ToBeVisibleAsync();
            await Button(page, "Delete").ClickAsync();
            await Expect(confirm).ToBeHiddenAsync();
            await Expect(rowDelete).ToHaveCountAsync(0);
            Assert.AreEqual(404, (await api.GetAsync($"{appUrl}/api/events/{eventId}")).Status,
                "Confirm must delete the Event.");
        }
        finally
        {
            // Child first: a Game with Activities refuses its delete. A 204 also covers an item already gone.
            if (eventId is not null) await api.DeleteAsync($"{appUrl}/api/events/{eventId}");
            if (activityId is not null) await api.DeleteAsync($"{appUrl}/api/activities/{activityId}");
            if (gameId is not null) await api.DeleteAsync($"{appUrl}/api/games/{gameId}");
        }
    }

    private static async Task OpenEventsPageAsync(IPage page, string appUrl)
    {
        await page.GotoAsync(appUrl + "/events", new() { Timeout = AppLoadTimeoutMs });
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Events", Level = 1, Exact = true }))
            .ToBeVisibleAsync(new() { Timeout = AppLoadTimeoutMs });
    }

    // Every page dialog is a FluentDialog whose accessible name is its title.
    private static ILocator Dialog(IPage page, string name) =>
        page.GetByRole(AriaRole.Dialog, new() { Name = name, Exact = true });

    private static ILocator Button(IPage page, string name) =>
        page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });

    // Calls the API through the emulator with the context's admin cookie and returns the saved item.
    private static async Task<JsonElement> PostAsync(IAPIRequestContext api, string appUrl, string path, object body)
    {
        var response = await api.PostAsync(appUrl + path, new() { DataObject = body });
        Assert.IsTrue(response.Ok, $"POST {path} returned {response.Status}: {await response.TextAsync()}");
        return (await response.JsonAsync())!.Value;
    }
}
