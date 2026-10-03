using Bunit;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Pages;

namespace MW_GC.EventManager.Web.Tests;

// One bUnit render per library page: the page loads its list from the stub API and shows it.
[TestClass]
public sealed class PageSmokeTests : IDisposable
{
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly GameEntity game = new() { Name = "Smoke game", Website = "https://example.test/smoke" };
    private readonly ThemeEntity theme = new() { Name = "Smoke theme" };
    private readonly HolidayEntity holiday = new() { Name = "Smoke holiday" };
    private readonly ActivityEntity activity;
    private readonly EventEntity ev;

    public PageSmokeTests()
    {
        activity = new ActivityEntity { Name = "Smoke activity", GameId = game.Id, ThemeIds = [theme.Id], HolidayIds = [holiday.Id] };
        ev = new EventEntity
        {
            Name = "Smoke night",
            Selections = [new() { Game = new Game { Id = game.Id, Name = game.Name }, Activity = new Activity { Id = activity.Id, GameId = game.Id, Name = activity.Name } }],
            WinnerActivityId = activity.Id
        };
        api.Json(HttpMethod.Get, "/api/games", new[] { game })
           .Json(HttpMethod.Get, "/api/activities", new[] { activity })
           .Json(HttpMethod.Get, "/api/themes", new[] { theme })
           .Json(HttpMethod.Get, "/api/holidays", new[] { holiday })
           .Json(HttpMethod.Get, "/api/events", new[] { ev });
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    [TestMethod]
    public void GamesPageRendersItsList()
    {
        var page = harness.Render<Games>();
        page.WaitForRows(1);
        var row = Assert.ContainsSingle(page.Rows());
        Assert.Contains("Smoke game", row);
        Assert.Contains("example.test/smoke", row);
        Assert.IsEmpty(page.PageAlerts());
        Assert.AreEqual(1, api.Count(HttpMethod.Get, "/api/games"));
    }

    [TestMethod]
    public void ActivitiesPageRendersItsList()
    {
        var page = harness.Render<Activities>();
        page.WaitForAssertion(() => Assert.Contains("Smoke theme", Assert.ContainsSingle(page.Rows())));
        var row = Assert.ContainsSingle(page.Rows());
        Assert.Contains("Smoke activity", row);
        Assert.Contains("Smoke game", row);
        Assert.Contains("Smoke holiday", row);
        Assert.IsEmpty(page.PageAlerts());
        Assert.AreEqual(1, api.Count(HttpMethod.Get, "/api/activities"));
    }

    [TestMethod]
    public void ThemesPageRendersItsList()
    {
        var page = harness.Render<Themes>();
        page.WaitForRows(1);
        Assert.Contains("Smoke theme", Assert.ContainsSingle(page.Rows()));
        Assert.IsEmpty(page.PageAlerts());
        Assert.AreEqual(1, api.Count(HttpMethod.Get, "/api/themes"));
    }

    [TestMethod]
    public void HolidaysPageRendersItsList()
    {
        var page = harness.Render<Holidays>();
        page.WaitForRows(1);
        Assert.Contains("Smoke holiday", Assert.ContainsSingle(page.Rows()));
        Assert.IsEmpty(page.PageAlerts());
        Assert.AreEqual(1, api.Count(HttpMethod.Get, "/api/holidays"));
    }

    [TestMethod]
    public void EventsPageRendersItsList()
    {
        var page = harness.Render<Events>();
        page.WaitForRows(1);
        var row = Assert.ContainsSingle(page.Rows());
        Assert.Contains("Smoke night", row);
        Assert.Contains("Smoke game — Smoke activity", row);
        Assert.IsEmpty(page.PageAlerts());
        Assert.AreEqual(1, api.Count(HttpMethod.Get, "/api/events"));
    }
}
