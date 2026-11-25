using Bunit;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Pages;
using MW_GC.EventManager.Web.Shared.Activities;

namespace MW_GC.EventManager.Web.Tests;

// #196.1: the three readers that used GameService (the Events page, the Stats page and the Activity details
// performance panel) read the Games through LibraryClient<GameEntity> now. The request they send is the same: one
// GET api/games each, and the Games it returns still name what they show.
[TestClass]
public sealed class GameReadersTests : IDisposable
{
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly GameEntity kart = new() { Name = "Kart" };
    private readonly ActivityEntity race;
    private readonly EventEntity night;

    public GameReadersTests()
    {
        race = new ActivityEntity { Name = "Race", GameId = kart.Id };
        night = new EventEntity
        {
            Name = "Kart night",
            Date = new DateTimeOffset(2020, 3, 1, 19, 0, 0, TimeSpan.Zero),
            Selections = [new() { Game = new Game { Id = kart.Id, Name = kart.Name }, Activity = new Activity { Id = race.Id, GameId = kart.Id, Name = race.Name } }],
            WinnerActivityId = race.Id
        };
        api.Json(HttpMethod.Get, "/api/games", new[] { kart })
           .Json(HttpMethod.Get, "/api/activities", new[] { race })
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>())
           .Json(HttpMethod.Get, "/api/events", new[] { night })
           .Json(HttpMethod.Get, "/api/event-metrics", Array.Empty<EventMetricsWithLikes>());
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    [TestMethod]
    public void EventsPage_SendsOneGamesRead()
    {
        var page = harness.Render<Events>();
        page.WaitForRows(1);

        Assert.AreEqual(1, api.Count(HttpMethod.Get, "/api/games"));
        Assert.IsEmpty(page.PageAlerts());
    }

    [TestMethod]
    public void StatsPage_SendsOneGamesRead()
    {
        var page = harness.Render<Stats>();
        // The Games table names Kart from the Games read (a Game missing from it would show as deleted).
        page.WaitForAssertion(() => Assert.IsTrue(page.FindAll("table.stats-games .stats-name").Any(n => n.TextContent.Trim() == "Kart")));

        Assert.AreEqual(1, api.Count(HttpMethod.Get, "/api/games"));
        Assert.IsEmpty(page.FindAll("table.stats-games .stats-deleted"));
        Assert.IsEmpty(page.PageAlerts());
    }

    // The panel on its own, so the Activities page's own Games read does not count.
    [TestMethod]
    public void ActivityDetails_PerformancePanel_SendsOneGamesRead()
    {
        var panel = harness.Render<ActivityPerformancePanel>(p => p.Add(x => x.ActivityId, race.Id));
        // P4: a count, which fails while the panel still loads (Find would throw, never return null).
        panel.WaitForAssertion(() => Assert.HasCount(1, panel.FindAll(".performance-offered")));

        Assert.AreEqual("1", panel.Find(".performance-offered").TextContent.Trim());
        Assert.AreEqual(1, api.Count(HttpMethod.Get, "/api/games"));
    }
}
