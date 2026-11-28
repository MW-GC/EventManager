using AngleSharp.Dom;
using Bunit;
using Microsoft.JSInterop;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// #261: the Copy feedback link button of the Event details dialog when the connection to the browser is gone. The
// clipboard call is answered through the harness's JS interop, the seam the Copy announcement tests use.
[TestClass]
public sealed class FeedbackLinkCopyTests : IDisposable
{
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly EventEntity night = new() { Name = "Game night", Date = new DateTimeOffset(2026, 12, 4, 19, 0, 0, TimeSpan.Zero) };

    public FeedbackLinkCopyTests()
    {
        api.Json(HttpMethod.Get, "/api/events", new[] { night })
           .Json(HttpMethod.Get, "/api/games", Array.Empty<GameEntity>())
           .Json(HttpMethod.Get, "/api/activities", Array.Empty<ActivityEntity>())
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>())
           .Json(HttpMethod.Get, $"/api/events/{night.Id}/likes", new { count = 0 })
           .Json(HttpMethod.Get, $"/api/events/{night.Id}/feedback-link", new { token = "tok-261" });
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    // A lost connection says so in the alert, shows no "copied" status, and nothing escapes the click.
    [TestMethod]
    public async Task ALostConnection_SaysSoInTheAlert_AndNoStatus()
    {
        var clipboard = harness.JSInterop.SetupVoid("navigator.clipboard.writeText", _ => true);
        clipboard.SetException(new JSDisconnectedException("gone"));
        var page = harness.Render<Events>();
        page.WaitForRows(1);
        await RowButton(page, "Game night", "Details").ClickAsync(new());

        await CopyButton(page).ClickAsync(new());

        Assert.AreEqual("http://localhost/feedback/tok-261", Assert.ContainsSingle(clipboard.Invocations).Arguments[0]);
        page.WaitForAssertion(() => CollectionAssert.AreEqual(
            new[] { "The feedback link could not be copied: the connection to the browser was lost." }, Alerts(page)));
        Assert.IsEmpty(Statuses(page), "no \"Feedback link copied.\"");
        Assert.IsTrue(page.HasFormDialog());
        Assert.IsFalse(harness.UnhandledException.IsCompleted);
    }

    private static IElement CopyButton(IRenderedComponent<Events> page) => page.FormDialog().Button("Copy feedback link");

    // The messages beside the button (the row it shares with Copy announcement).
    private static List<string> Statuses(IRenderedComponent<Events> page) =>
        CopyButton(page).ParentElement!.QuerySelectorAll("[role=status]").Select(s => s.TextContent.Trim()).ToList();

    private static List<string> Alerts(IRenderedComponent<Events> page) =>
        CopyButton(page).ParentElement!.QuerySelectorAll("[role=alert]").Select(s => s.TextContent.Trim()).ToList();
}
