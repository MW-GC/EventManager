using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Pages;

namespace MW_GC.EventManager.Web.Tests;

// Editing an Event keeps each slot's stored Selection snapshot until the user changes that slot,
// even when the library has since deleted or rewritten the Game or Activity (issue #53).
[TestClass]
public sealed class EditEventSnapshotTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly GameEntity kart = new() { Name = "Kart" };
    private readonly GameEntity brawl = new() { Name = "Brawl" };
    private readonly ActivityEntity grandPrix;
    private readonly ActivityEntity stock;
    private readonly ActivityEntity teams;
    private readonly ActivityEntity timeTrial;
    private EventEntity ev = new();

    public EditEventSnapshotTests()
    {
        grandPrix = new ActivityEntity { Name = "Grand Prix", GameId = kart.Id, Description = "Four races", Rules = "150cc", SetupRequirements = "Four controllers", Comments = "Ran long" };
        timeTrial = new ActivityEntity { Name = "Time Trial", GameId = kart.Id };
        stock = new ActivityEntity { Name = "Stock Battle", GameId = brawl.Id, Description = "Three lives", Rules = "No items", SetupRequirements = "One TV", Comments = "Crowd favourite" };
        teams = new ActivityEntity { Name = "Team Battle", GameId = brawl.Id };
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    [TestMethod]
    public async Task EditEvent_ActivityDeleted_ShowsDeletedLabelAndSavesNameChangeWithSlotUnchanged()
    {
        StoreEvent(winner: grandPrix);
        UseLibrary([kart, brawl], [grandPrix, timeTrial, teams]); // Stock Battle was deleted.
        var page = await OpenEdit();

        Assert.AreEqual("Brawl", SelectedText(page, 1, Picker.Game));
        Assert.AreEqual("(deleted) Stock Battle", SelectedText(page, 1, Picker.Activity));
        Assert.AreEqual("Grand Prix", SelectedText(page, 0, Picker.Activity));
        Assert.IsEmpty(page.Dialog().Alerts());

        var saved = await SaveWithName(page, "Renamed night");

        Assert.AreEqual("Renamed night", saved.Name);
        AssertSameSelections(ev.Selections, saved.Selections);
        Assert.AreEqual(grandPrix.Id, saved.WinnerActivityId);
        Assert.IsFalse(page.HasDialog());
    }

    [TestMethod]
    public async Task EditEvent_GameDeleted_ShowsDeletedLabelAndKeepsSnapshot()
    {
        StoreEvent(winner: stock);
        UseLibrary([kart], [grandPrix, timeTrial]); // Brawl and its activities were deleted.
        var page = await OpenEdit();

        Assert.AreEqual("(deleted) Brawl", SelectedText(page, 1, Picker.Game));
        Assert.AreEqual("(deleted) Stock Battle", SelectedText(page, 1, Picker.Activity));
        Assert.AreEqual("Kart", SelectedText(page, 0, Picker.Game));
        // The deleted Game is only the current value of its own slot, never a choice elsewhere.
        CollectionAssert.DoesNotContain(OptionTexts(page, 0, Picker.Game), "(deleted) Brawl");

        var saved = await SaveWithName(page, "Renamed night");

        AssertSameSelections(ev.Selections, saved.Selections);
        Assert.AreEqual(stock.Id, saved.WinnerActivityId);
    }

    [TestMethod]
    public async Task EditEvent_UntouchedSlot_KeepsStoredTextWhenLibraryTextChanged()
    {
        StoreEvent(winner: null);
        var rewritten = new ActivityEntity
        {
            Id = grandPrix.Id, GameId = kart.Id, Name = grandPrix.Name, Description = "New description",
            Rules = "200cc", SetupRequirements = "Eight controllers", Comments = "A newer note"
        };
        var renamedGame = new GameEntity { Id = kart.Id, Name = kart.Name, IconUrl = "https://example.test/new.png" };
        UseLibrary([renamedGame, brawl], [rewritten, timeTrial, stock, teams]);
        var page = await OpenEdit();

        var saved = await SaveWithName(page, "Renamed night");

        AssertSameSelections(ev.Selections, saved.Selections);
        Assert.AreEqual("Four races", saved.Selections[0].Activity.Description);
        Assert.AreEqual("Ran long", saved.Selections[0].Activity.Comments);
        Assert.IsNull(saved.WinnerActivityId);
    }

    [TestMethod]
    public async Task EditEvent_ReplacingWinnerSlot_ShowsNoticeAndClearsWinner()
    {
        StoreEvent(winner: grandPrix);
        UseLibrary([kart, brawl], [grandPrix, timeTrial, teams]); // Stock Battle was deleted.
        var page = await OpenEdit();
        Assert.IsEmpty(Notices(page));

        await Choose(page, 0, Picker.Activity, timeTrial);

        Assert.AreEqual("Time Trial", SelectedText(page, 0, Picker.Activity));
        Assert.AreEqual(ReplaceNotice, Assert.ContainsSingle(Notices(page, slot: 0)));
        Assert.IsEmpty(Notices(page, slot: 1));
        var saved = await SaveWithName(page, ev.Name);

        Assert.IsNull(saved.WinnerActivityId);
        Assert.AreEqual(timeTrial.Id, saved.Selections[0].Activity.Id);
        Assert.AreEqual(kart.Id, saved.Selections[0].Game.Id);
        // The other slot is still its stored snapshot, deleted Activity and all.
        AssertSameSelections(ev.Selections.Skip(1), saved.Selections.Skip(1));
    }

    [TestMethod]
    public async Task EditEvent_ReplacingNonWinnerSlot_KeepsWinner()
    {
        StoreEvent(winner: grandPrix);
        UseLibrary([kart, brawl], [grandPrix, timeTrial, teams]); // Stock Battle was deleted.
        var page = await OpenEdit();

        await Choose(page, 1, Picker.Activity, teams);

        Assert.AreEqual("Team Battle", SelectedText(page, 1, Picker.Activity));
        Assert.IsEmpty(Notices(page));
        var saved = await SaveWithName(page, ev.Name);

        Assert.AreEqual(grandPrix.Id, saved.WinnerActivityId);
        Assert.AreEqual(teams.Id, saved.Selections[1].Activity.Id);
        AssertSameSelections(ev.Selections.Take(1), saved.Selections.Take(1));
        // A replaced slot is rebuilt from today's library.
        Assert.AreEqual("Team Battle", saved.Selections[1].Activity.Name);
    }

    private const string ReplaceNotice = "Replacing this activity clears the Winner.";

    private enum Picker { Game = 0, Activity = 1 }

    // The Event as stored: two slots whose snapshots carry the library text of the day they were saved.
    private void StoreEvent(ActivityEntity? winner)
    {
        ev = new EventEntity
        {
            Name = "Game night",
            Date = new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero),
            Selections = [Snapshot(kart, grandPrix), Snapshot(brawl, stock)],
            WinnerActivityId = winner?.Id
        };
    }

    private static Selection Snapshot(GameEntity game, ActivityEntity act) => new()
    {
        Game = new Game { Id = game.Id, Name = game.Name, IconUrl = "https://example.test/old.png" },
        Activity = new Activity
        {
            Id = act.Id, GameId = act.GameId, Name = act.Name, Description = act.Description,
            Rules = act.Rules, SetupRequirements = act.SetupRequirements, Comments = act.Comments
        }
    };

    private void UseLibrary(GameEntity[] games, ActivityEntity[] activities)
    {
        api.Json(HttpMethod.Get, "/api/events", new[] { ev })
           .Json(HttpMethod.Get, "/api/games", games)
           .Json(HttpMethod.Get, "/api/activities", activities)
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>());
    }

    private async Task<IRenderedComponent<Events>> OpenEdit()
    {
        var page = harness.Render<Events>();
        page.WaitForRows(1);
        await page.Find("fluent-button[title=Edit]").ClickAsync(new());
        Assert.AreEqual("Edit Event", page.DialogTitle());
        return page;
    }

    // Types a name, presses Update Event, and returns the Event the page sent.
    private async Task<EventEntity> SaveWithName(IRenderedComponent<Events> page, string name)
    {
        var path = $"/api/events/{ev.Id}";
        api.On(HttpMethod.Put, path, () => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(api.Bodies(HttpMethod.Put, path).Last()!, System.Text.Encoding.UTF8, "application/json")
        });
        await page.Find("fluent-dialog fluent-text-field[placeholder='Enter event name...']").ChangeAsync(new ChangeEventArgs { Value = name });
        await page.Dialog().Button("Update Event").ClickAsync(new());
        Assert.IsFalse(page.HasDialog(), string.Join(" | ", page.HasDialog() ? page.Dialog().Alerts() : []));
        return JsonSerializer.Deserialize<EventEntity>(Assert.ContainsSingle(api.Bodies(HttpMethod.Put, path))!, Web)!;
    }

    // Compares every stored field, including description, rules, setup requirements and comments.
    private static void AssertSameSelections(IEnumerable<Selection> expected, IEnumerable<Selection> actual) =>
        Assert.AreSequenceEqual(expected.Select(s => JsonSerializer.Serialize(s, Web)).ToArray(),
            actual.Select(s => JsonSerializer.Serialize(s, Web)).ToArray());

    private static List<IElement> Slots(IRenderedComponent<Events> page) =>
        page.Dialog().QuerySelectorAll("fluent-card")
            .Where(c => c.QuerySelector("fluent-button[aria-label^='Re-roll slot']") is not null
                        && c.QuerySelectorAll("fluent-card").Length == 0)
            .ToList();

    private static IElement Select(IRenderedComponent<Events> page, int slot, Picker picker) =>
        Slots(page)[slot].QuerySelectorAll("fluent-select")[(int)picker];

    private static string SelectedText(IRenderedComponent<Events> page, int slot, Picker picker)
    {
        var select = Select(page, slot, picker);
        var value = select.GetAttribute("current-value");
        return select.QuerySelectorAll("fluent-option").Single(o => o.GetAttribute("value") == value).TextContent.Trim();
    }

    private static List<string> OptionTexts(IRenderedComponent<Events> page, int slot, Picker picker) =>
        Select(page, slot, picker).QuerySelectorAll("fluent-option").Select(o => o.TextContent.Trim()).ToList();

    private static async Task Choose(IRenderedComponent<Events> page, int slot, Picker picker, EntityBase choice) =>
        await Select(page, slot, picker).QuerySelectorAll("fluent-option")
            .Single(o => o.GetAttribute("value") == choice.Id.ToString()).ClickAsync(new());

    private static List<string> Notices(IRenderedComponent<Events> page, int? slot = null) =>
        (slot is { } i ? [Slots(page)[i]] : Slots(page))
            .SelectMany(c => c.QuerySelectorAll("[role=status]"))
            .Select(s => s.TextContent.Trim()).Where(t => t == ReplaceNotice).ToList();
}
