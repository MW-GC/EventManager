using System.Net;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Pages;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// #98: a Selection snapshot keeps the players and duration its Activity had when it was chosen, and the Event
// details dialog shows them from the snapshot; a snapshot without them (stored before #98) shows neither.
[TestClass]
public sealed class EventPlayersDurationTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly GameEntity kart = new() { Name = "Kart" };
    private readonly GameEntity brawl = new() { Name = "Brawl" };
    private readonly ActivityEntity grandPrix;
    private readonly ActivityEntity stock;
    private readonly ActivityEntity teams;
    private readonly EventEntity night;

    public EventPlayersDurationTests()
    {
        grandPrix = new ActivityEntity { Name = "Grand Prix", GameId = kart.Id, MinPlayers = 2, MaxPlayers = 6, DurationMinutes = 45 };
        stock = new ActivityEntity { Name = "Stock Battle", GameId = brawl.Id };
        teams = new ActivityEntity { Name = "Team Battle", GameId = brawl.Id, MaxPlayers = 4, DurationMinutes = 20 };
        night = new EventEntity
        {
            Name = "Game night",
            Date = new DateTimeOffset(2026, 12, 4, 19, 0, 0, TimeSpan.Zero),
            Selections =
            [
                new Selection
                {
                    Game = new Game { Id = kart.Id, Name = "Kart" },
                    Activity = new Activity { Id = grandPrix.Id, GameId = kart.Id, Name = "Grand Prix", MinPlayers = 2, MaxPlayers = 6, DurationMinutes = 45 }
                },
                // As an Event stored before #98 holds it: no players or duration in the snapshot.
                Pick(brawl, stock),
            ]
        };
        api.Json(HttpMethod.Get, "/api/events", new[] { night })
           .Json(HttpMethod.Get, "/api/games", new[] { kart, brawl })
           .Json(HttpMethod.Get, "/api/activities", new[] { grandPrix, stock, teams })
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>());
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    // ---- the details dialog --------------------------------------------------------------------

    [TestMethod]
    public async Task Details_ShowThePlayersAndDurationOfASnapshotThatHasThem()
    {
        var page = await OpenDetails();

        var card = Card(page, "Grand Prix");
        Assert.AreEqual("2-6 players", card.QuerySelector(".selection-players")?.TextContent.Trim());
        Assert.AreEqual("45 min", card.QuerySelector(".selection-duration")?.TextContent.Trim());
    }

    [TestMethod]
    public async Task Details_ShowNeitherForASnapshotWithout()
    {
        var page = await OpenDetails();

        var card = Card(page, "Stock Battle");
        Assert.IsNull(card.QuerySelector(".selection-players"));
        Assert.IsNull(card.QuerySelector(".selection-duration"));
        Assert.DoesNotContain("players", card.TextContent);
        Assert.DoesNotContain(" min", card.TextContent);
    }

    // The dialog reads the snapshot, never today's Library: the Library's Grand Prix changes, the dialog does not.
    [TestMethod]
    public async Task Details_ShowTheSnapshotsValues_NotTheLibrarys()
    {
        grandPrix.MinPlayers = 8;
        grandPrix.MaxPlayers = null;
        grandPrix.DurationMinutes = 120;
        api.Json(HttpMethod.Get, "/api/activities", new[] { grandPrix, stock, teams });

        var page = await OpenDetails();

        var card = Card(page, "Grand Prix");
        Assert.AreEqual("2-6 players", card.QuerySelector(".selection-players")?.TextContent.Trim());
        Assert.AreEqual("45 min", card.QuerySelector(".selection-duration")?.TextContent.Trim());
    }

    // ---- the snapshot copy ---------------------------------------------------------------------

    // Choosing an Activity in a slot copies its players and duration into the Selection the Event saves.
    [TestMethod]
    public async Task ChoosingAnActivity_CopiesItsPlayersAndDurationIntoTheSnapshot()
    {
        var page = await OpenEdit();

        await Choose(page, 1, teams);
        var saved = await Save(page);

        var activity = saved.Selections[1].Activity;
        Assert.AreEqual(teams.Id, activity.Id);
        Assert.IsNull(activity.MinPlayers);
        Assert.AreEqual(4, activity.MaxPlayers);
        Assert.AreEqual(20, activity.DurationMinutes);
        // The untouched slot keeps its stored snapshot, values and all.
        Assert.AreEqual(2, saved.Selections[0].Activity.MinPlayers);
        Assert.AreEqual(6, saved.Selections[0].Activity.MaxPlayers);
        Assert.AreEqual(45, saved.Selections[0].Activity.DurationMinutes);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private async Task<IRenderedComponent<Events>> OpenDetails()
    {
        var page = harness.Render<Events>();
        page.WaitForRows(1);
        await RowButton(page, "Game night", "Details").ClickAsync(new());
        Assert.IsTrue(page.HasFormDialog());
        return page;
    }

    private async Task<IRenderedComponent<Events>> OpenEdit()
    {
        var page = harness.Render<Events>();
        page.WaitForRows(1);
        await page.Find("fluent-button[title=Edit]").ClickAsync(new());
        Assert.AreEqual("Edit Event", page.DialogTitle());
        return page;
    }

    private async Task<EventEntity> Save(IRenderedComponent<Events> page)
    {
        var path = $"/api/events/{night.Id}";
        api.On(HttpMethod.Put, path, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(api.Bodies(HttpMethod.Put, path).Last()!, System.Text.Encoding.UTF8, "application/json")
        });
        await page.Dialog().Button("Update Event").ClickAsync(new());
        Assert.IsFalse(page.HasDialog(), string.Join(" | ", page.HasDialog() ? page.Dialog().Alerts() : []));
        return JsonSerializer.Deserialize<EventEntity>(Assert.ContainsSingle(api.Bodies(HttpMethod.Put, path))!, Web)!;
    }

    // One slot's Activity picker (the second select of the slot's card).
    private static async Task Choose(IRenderedComponent<Events> page, int slot, ActivityEntity choice)
    {
        var slots = page.Dialog().QuerySelectorAll("fluent-card")
            .Where(c => c.QuerySelector("fluent-button[aria-label^='Re-roll slot']") is not null && c.QuerySelectorAll("fluent-card").Length == 0)
            .ToList();
        await slots[slot].QuerySelectorAll("fluent-select")[1].QuerySelectorAll("fluent-option")
            .Single(o => o.GetAttribute("value") == choice.Id.ToString()).ClickAsync(new());
    }
}
