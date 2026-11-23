using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// Parked by the wave 1 reviews (P3): while a row's delete runs, the page's own AskDelete guard is the only thing that
// stops a click on another row's Delete from opening a second question. The disabled button hides it from a real
// click (Fluent drops clicks on a disabled button), so these take the row's click handler while it is enabled and run
// it after the delete started, as a click that lands before the re-render does. On the Events page the guard is
// `if (_rows.BusyId is not null) return;`, on the shared Library page `if (_rows.AnyBusy) return;`.
[TestClass]
public sealed class AskDeleteGuardTests : IDisposable
{
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;

    public AskDeleteGuardTests() => harness = new PageHarness(api);

    public void Dispose() => harness.Dispose();

    [TestMethod]
    public async Task EventsPage_AskDeleteWhileADeleteRuns_OpensNoSecondQuestion_AndSendsOneDelete()
    {
        var night = new EventEntity { Name = "Game night", Date = new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero) };
        var path = $"/api/events/{night.Id}";
        api.Json(HttpMethod.Get, "/api/games", Array.Empty<GameEntity>())
           .Json(HttpMethod.Get, "/api/activities", Array.Empty<ActivityEntity>())
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>())
           .Json(HttpMethod.Get, "/api/events", new[] { night });
        var gate = api.Hold(HttpMethod.Delete, path, () => new HttpResponseMessage(HttpStatusCode.NoContent));
        var page = harness.Render<Events>();
        page.WaitForRows(1);

        await AskAgainWhileTheDeleteRuns(page, "Game night", gate);

        Assert.AreEqual(1, api.Count(HttpMethod.Delete, path));
    }

    [TestMethod]
    public async Task LibraryPage_AskDeleteWhileADeleteRuns_OpensNoSecondQuestion_AndSendsOneDelete()
    {
        var spooky = new ThemeEntity { Name = "Spooky" };
        var path = $"/api/themes/{spooky.Id}";
        api.Json(HttpMethod.Get, "/api/themes", new[] { spooky });
        var gate = api.Hold(HttpMethod.Delete, path, () => new HttpResponseMessage(HttpStatusCode.NoContent));
        var page = harness.Render<Themes>();
        page.WaitForRows(1);

        await AskAgainWhileTheDeleteRuns(page, "Spooky", gate);

        Assert.AreEqual(1, api.Count(HttpMethod.Delete, path));
    }

    // Deletes the row (held at the API), then runs the row's Delete click handler again while that delete runs.
    private static async Task AskAgainWhileTheDeleteRuns<T>(IRenderedComponent<T> page, string row, TaskCompletionSource gate)
        where T : class, IComponent
    {
        // The one Delete on the page (the row's icon button), taken while it is enabled.
        var askDelete = ClickHandler(page, "Delete");
        await RowButton(page, row, "Delete").ClickAsync(new());
        Assert.IsTrue(page.HasConfirmation(), "the first Delete asks");
        var first = page.ConfirmAsync();
        page.WaitForAssertion(() =>
        {
            Assert.IsFalse(page.HasConfirmation(), "the question closed and the delete is running");
            Assert.IsTrue(RowButton(page, row, "Delete").HasAttribute("disabled"), "the row's Delete is disabled while it runs");
        });

        await page.InvokeAsync(() => askDelete.InvokeAsync(new()));

        Assert.IsFalse(page.HasConfirmation(), "a second question opened while the delete ran");
        gate.SetResult();
        await first;
        Assert.IsFalse(page.HasConfirmation());
    }
}
