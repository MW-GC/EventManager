using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using MW_GC.EventManager.Web.Shared.Library;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// #98 part 2: the players tonight and longest duration boxes in the create-and-edit Event dialog's Filters, on the
// real Events page. Empty is off; a valid value turns its Filter on and the rolls and pickers honour it; a value that
// is not a whole number in range shows the box's message and leaves its Filter off. The library has two Games, so a
// new Event rolls one slot on each, and Brawl's slot can hold Stock (1-4 players, 45 min) or Plain (no values).
[TestClass]
public sealed class PlayersDurationFilterBoxesTests : IDisposable
{
    private const string Players = "Players tonight";
    private const string Duration = "Longest duration (minutes)";
    private const string PlayersMessage = "Players tonight must be a whole number from 1 to 99.";
    private const string DurationMessage = "Longest duration must be a whole number from 1 to 1440.";
    private const string Hint = "Filters apply when randomizing all selections or re-rolling a slot";
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly GameEntity kart = new() { Name = "Kart" };
    private readonly GameEntity brawl = new() { Name = "Brawl" };
    private readonly ActivityEntity race;
    private readonly ActivityEntity stock;
    private readonly ActivityEntity plain;

    public PlayersDurationFilterBoxesTests()
    {
        race = new ActivityEntity { Name = "Race", GameId = kart.Id, MinPlayers = 2, MaxPlayers = 6, DurationMinutes = 20 };
        stock = new ActivityEntity { Name = "Stock", GameId = brawl.Id, MinPlayers = 1, MaxPlayers = 4, DurationMinutes = 45 };
        plain = new ActivityEntity { Name = "Plain", GameId = brawl.Id };
        api.Json(HttpMethod.Get, "/api/games", new[] { kart, brawl })
           .Json(HttpMethod.Get, "/api/activities", new[] { race, stock, plain })
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>())
           .Json(HttpMethod.Get, "/api/events", Array.Empty<EventEntity>());
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    // ---- on ------------------------------------------------------------------------------------

    // A slot's picker always lists the slot's own Activity, so each check below rolls first: with the Filter on, the
    // roll puts Plain in Brawl's slot, and the picker then offers Stock only while the Filter is off.
    [TestMethod]
    [DataRow(Players, "5")]
    [DataRow(Players, " 5 ")]
    [DataRow(Duration, "30")]
    public async Task AValidValue_TurnsItsFilterOn_AndRollsAndPickersHonourIt(string box, string text)
    {
        var page = await OpenCreate();

        await Type(page, box, text);

        Assert.IsEmpty(page.FormDialog().Alerts());
        for (var i = 0; i < 20; i++)
        {
            await page.FormDialog().Button("Randomize All").ClickAsync(new());
            Assert.AreEqual(plain.Id.ToString(), BrawlActivity(page), $"roll {i} picked an Activity the Filter excludes");
        }
        Assert.AreSequenceEqual(new[] { plain.Id.ToString() }, BrawlActivityOptions(page).ToArray());
    }

    // ---- off -----------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(Players)]
    [DataRow(Duration)]
    public async Task ClearingABox_TurnsItsFilterOff(string box)
    {
        var page = await OpenCreate();
        await TurnOnAndRoll(page, box);
        Assert.DoesNotContain(stock.Id.ToString(), BrawlActivityOptions(page));

        await Type(page, box, "");

        Assert.Contains(stock.Id.ToString(), BrawlActivityOptions(page));
        Assert.IsEmpty(page.FormDialog().Alerts());
        Assert.DoesNotContain(Hint, page.FormDialog().TextContent);
    }

    [TestMethod]
    [DataRow(Players, "abc", PlayersMessage)]
    [DataRow(Players, "0", PlayersMessage)]
    [DataRow(Players, "-3", PlayersMessage)]
    [DataRow(Players, "2.5", PlayersMessage)]
    [DataRow(Players, "100", PlayersMessage)]
    // Whole numbers written as a decimal, an exponent, with a thousands separator or in brackets are refused too.
    [DataRow(Players, "2.0", PlayersMessage)]
    [DataRow(Players, "1e1", PlayersMessage)]
    [DataRow(Players, "1,0", PlayersMessage)]
    [DataRow(Players, "(5)", PlayersMessage)]
    [DataRow(Duration, "abc", DurationMessage)]
    [DataRow(Duration, "0", DurationMessage)]
    [DataRow(Duration, "-30", DurationMessage)]
    [DataRow(Duration, "30.5", DurationMessage)]
    [DataRow(Duration, "1441", DurationMessage)]
    [DataRow(Duration, "2.0", DurationMessage)]
    [DataRow(Duration, "1e1", DurationMessage)]
    [DataRow(Duration, "1,0", DurationMessage)]
    [DataRow(Duration, "(5)", DurationMessage)]
    public async Task AValueThatIsNotValid_ShowsTheBoxsMessage_AndLeavesItsFilterOff(string box, string text, string message)
    {
        var page = await OpenCreate();
        // A valid value first, so a bad value that kept (or used) the old one would still filter.
        await TurnOnAndRoll(page, box);

        await Type(page, box, text);

        Assert.AreEqual(message, Assert.ContainsSingle(page.FormDialog().Alerts()));
        Assert.AreEqual(message, Assert.ContainsSingle(Box(page, box).ParentElement!.Alerts()));
        Assert.AreEqual("true", Box(page, box).GetAttribute("aria-invalid"));
        Assert.AreEqual(text, Box(page, box).GetAttribute("value"));
        CollectionAssert.AreEquivalent(new[] { stock.Id.ToString(), plain.Id.ToString() }, BrawlActivityOptions(page));
        Assert.DoesNotContain(Hint, page.FormDialog().TextContent);

        // Fixing it turns the Filter on and the message goes.
        await Type(page, box, Excluding(box));
        Assert.IsEmpty(page.FormDialog().Alerts());
        Assert.IsNull(Box(page, box).GetAttribute("aria-invalid"));
        Assert.DoesNotContain(stock.Id.ToString(), BrawlActivityOptions(page));
    }

    // ---- accessible names and the hint ---------------------------------------------------------

    // The field is found through its visible label, never by the name being checked.
    [TestMethod]
    [DataRow(Players, "filter-players-tonight", "filter-players-tonight-message")]
    [DataRow(Duration, "filter-longest-duration", "filter-longest-duration-message")]
    public async Task EachBox_HasItsAccessibleName_AndNamesItsMessage(string box, string id, string messageId)
    {
        var page = await OpenCreate();

        var label = page.FormDialog().QuerySelectorAll("label").Single(l => l.TextContent.Trim() == box);
        Assert.AreEqual(id, label.GetAttribute("for"));
        var field = page.FormDialog().QuerySelector($"fluent-text-field#{id}")
            ?? throw new AssertFailedException($"No box with the id {id}.");
        Assert.AreEqual(box, field.GetAttribute("aria-label"));
        Assert.AreEqual(messageId, field.GetAttribute("aria-describedby"));
        var holder = page.Find($"#{messageId}");
        Assert.AreSame(field.ParentElement, holder.ParentElement);
    }

    // The message is inside the element the box names, so a screen reader reads it with the box.
    [TestMethod]
    [DataRow(Players, PlayersMessage)]
    [DataRow(Duration, DurationMessage)]
    public async Task ABoxsMessage_SitsInsideTheHolderItsBoxNames(string box, string message)
    {
        var page = await OpenCreate();

        await Type(page, box, "abc");

        var holder = page.Find($"[id='{Box(page, box).GetAttribute("aria-describedby")}']");
        Assert.AreEqual(message, Assert.ContainsSingle(holder.Alerts()));
    }

    // Each message is linked into its field's shadow root by the module the Activities editor uses.
    [TestMethod]
    public async Task TheBoxes_LinkTheirMessagesIntoTheFieldsShadowRoots()
    {
        var module = harness.JSInterop.SetupModule(LibraryFieldMessage.Module);

        var page = await OpenCreate();

        page.WaitForAssertion(() => CollectionAssert.AreEquivalent(
            new[] { "filter-players-tonight-message", "filter-longest-duration-message" },
            module.Invocations["link"].Select(i => (string)i.Arguments[0]!).ToList()));
        var named = new[] { Players, Duration }.Select(b => Box(page, b).GetAttribute("aria-describedby")).ToList();
        CollectionAssert.AreEquivalent(named, module.Invocations["link"].Select(i => (string)i.Arguments[0]!).ToList());
    }

    [TestMethod]
    [DataRow(Players, "5")]
    [DataRow(Duration, "30")]
    public async Task TheHintLine_ShowsWhileEitherFilterIsOn(string box, string text)
    {
        var page = await OpenCreate();
        Assert.DoesNotContain(Hint, page.FormDialog().TextContent);

        await Type(page, box, text);

        Assert.Contains(Hint, page.FormDialog().TextContent);
    }

    // ---- unsaved changes and reopening ---------------------------------------------------------

    // As a ticked Theme or Holiday Filter counts (NonTextInputDirtyTests), so does a typed box; emptying it again
    // makes the dialog clean. Nothing is ever sent.
    [TestMethod]
    [DataRow(Players, "5")]
    [DataRow(Duration, "abc")]
    public async Task TypingInABox_IsAnUnsavedChange_UntilItIsEmptiedAgain(string box, string text)
    {
        var page = await OpenCreate();
        await Type(page, box, text);

        await Dismiss(page);
        Assert.IsTrue(page.HasConfirmation(), "typing in a Filter box did not count as a change");
        await page.Confirmation().Button("Keep editing").ClickAsync(new());

        await Type(page, box, "");
        await Dismiss(page);
        Assert.IsFalse(page.HasConfirmation());
        Assert.IsFalse(page.HasFormDialog());
        Assert.IsEmpty(api.Requests.Where(r => r.Method != HttpMethod.Get).ToList());
    }

    [TestMethod]
    public async Task ReopeningTheDialog_StartsWithBothBoxesEmptyAndOff()
    {
        var page = await OpenCreate();
        await Type(page, Players, "5");
        await Type(page, Duration, "abc");
        await page.FormDialog().Button("Cancel").ClickAsync(new());

        await page.Button("Create Event").ClickAsync(new());

        Assert.AreEqual(string.Empty, Box(page, Players).GetAttribute("value") ?? string.Empty);
        Assert.AreEqual(string.Empty, Box(page, Duration).GetAttribute("value") ?? string.Empty);
        Assert.IsEmpty(page.FormDialog().Alerts());
        Assert.Contains(stock.Id.ToString(), BrawlActivityOptions(page));
    }

    // Opening Edit on an Event starts the boxes again too. The Event's Brawl slot holds Plain, so its picker offers
    // Stock only while both Filters are off.
    [TestMethod]
    public async Task OpeningEdit_AfterTypingInACreateDialog_StartsWithBothBoxesEmptyAndOff()
    {
        var night = new EventEntity
        {
            Name = "Game night",
            Date = new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero),
            Selections = [Pick(kart, race), Pick(brawl, plain)]
        };
        api.Json(HttpMethod.Get, "/api/events", new[] { night });
        var page = await OpenCreate();
        page.WaitForAssertion(() => Assert.IsTrue(page.Rows().Any(r => r.Contains("Game night"))));
        await Type(page, Players, "5");
        await Type(page, Duration, "30");
        Assert.Contains(Hint, page.FormDialog().TextContent);
        await page.FormDialog().Button("Cancel").ClickAsync(new());

        await RowButton(page, "Game night", "Edit").ClickAsync(new());

        Assert.AreEqual(string.Empty, Box(page, Players).GetAttribute("value") ?? string.Empty);
        Assert.AreEqual(string.Empty, Box(page, Duration).GetAttribute("value") ?? string.Empty);
        Assert.IsEmpty(page.FormDialog().Alerts());
        Assert.DoesNotContain(Hint, page.FormDialog().TextContent);
        CollectionAssert.AreEquivalent(new[] { stock.Id.ToString(), plain.Id.ToString() }, BrawlActivityOptions(page));
        // The text typed in the create dialog is not this dialog's starting text: emptying both boxes changes nothing.
        await Type(page, Players, "");
        await Type(page, Duration, "");
        await Dismiss(page);
        Assert.IsFalse(page.HasConfirmation());
        Assert.IsFalse(page.HasFormDialog());
    }

    // ---- helpers -------------------------------------------------------------------------------

    private async Task<IRenderedComponent<Events>> OpenCreate()
    {
        var page = harness.Render<Events>();
        page.WaitForAssertion(() => Assert.AreEqual(5, api.Requests.Count(r => r.Method == HttpMethod.Get)));
        await page.Button("Create Event").ClickAsync(new());
        Assert.IsTrue(page.HasFormDialog());
        Assert.HasCount(2, Slots(page));
        return page;
    }

    // A valid value that excludes Stock (1-4 players, 45 min) and keeps Race (2-6 players, 20 min) and Plain.
    private static string Excluding(string box) => box == Players ? "5" : "30";

    // Turns the box's Filter on and rolls, so Brawl's slot holds Plain.
    private async Task TurnOnAndRoll(IRenderedComponent<Events> page, string box)
    {
        await Type(page, box, Excluding(box));
        await page.FormDialog().Button("Randomize All").ClickAsync(new());
        Assert.AreEqual(plain.Id.ToString(), BrawlActivity(page));
    }

    private static IElement Box(IRenderedComponent<Events> page, string label) =>
        page.FormDialog().QuerySelector($"fluent-text-field[aria-label='{label}']")
            ?? throw new AssertFailedException($"No box named {label}.");

    private static Task Type(IRenderedComponent<Events> page, string label, string text) =>
        Box(page, label).InputAsync(new ChangeEventArgs { Value = text });

    private static List<IElement> Slots(IRenderedComponent<Events> page) =>
        page.FormDialog().QuerySelectorAll("fluent-card")
            .Where(c => c.QuerySelector("fluent-button[aria-label^='Re-roll slot']") is not null && c.QuerySelectorAll("fluent-card").Length == 0)
            .ToList();

    // The Activity picker of the slot that holds Brawl.
    private IElement BrawlActivityPicker(IRenderedComponent<Events> page) =>
        Slots(page).Single(s => s.QuerySelectorAll("fluent-select")[0].GetAttribute("current-value") == brawl.Id.ToString())
            .QuerySelectorAll("fluent-select")[1];

    private List<string> BrawlActivityOptions(IRenderedComponent<Events> page) =>
        BrawlActivityPicker(page).QuerySelectorAll("fluent-option").Select(o => o.GetAttribute("value")!).ToList();

    private string? BrawlActivity(IRenderedComponent<Events> page) => BrawlActivityPicker(page).GetAttribute("current-value");
}
