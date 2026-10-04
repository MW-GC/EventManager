using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Pages;
using MW_GC.EventManager.Web.Shared;

namespace MW_GC.EventManager.Web.Tests;

// Issue #52 on all five pages, rendered with bUnit against a stub API: every Delete asks first,
// row actions and Select Winner run once, a late save never closes a newer dialog, and overlay
// click or Escape asks before discarding typed changes.
//
// Overlay click and Escape reach a page as the "ondialogdismiss" event that Fluent UI raises on
// <fluent-dialog> (the browser "dismiss" event), so Dismiss() triggers exactly that event.
[TestClass]
public sealed class DialogSafetyTests : IDisposable
{
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly GameEntity kart = new() { Name = "Kart" };
    private readonly GameEntity brawl = new() { Name = "Brawl" };
    private readonly GameEntity puzzle = new() { Name = "Puzzle" };
    private readonly ActivityEntity grandPrix;
    private readonly ActivityEntity timeTrial;
    private readonly ActivityEntity stock;
    // Used by the "Party" Event but no longer in the library.
    private readonly Activity retired;
    private readonly ThemeEntity spooky = new() { Name = "Spooky" };
    private readonly HolidayEntity halloween = new() { Name = "Halloween" };
    private readonly EventEntity night;
    private readonly EventEntity party;

    public DialogSafetyTests()
    {
        grandPrix = new ActivityEntity { Name = "Grand Prix", GameId = kart.Id };
        timeTrial = new ActivityEntity { Name = "Time Trial", GameId = kart.Id };
        stock = new ActivityEntity { Name = "Stock", GameId = brawl.Id };
        retired = new Activity { Id = Guid.NewGuid(), GameId = brawl.Id, Name = "Retired", Comments = "Old note" };
        night = new EventEntity
        {
            Name = "Game night",
            Date = new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero),
            UniqueGamesOnly = false,
            Selections = [Pick(kart, grandPrix), Pick(brawl, stock), Pick(kart, timeTrial)]
        };
        party = new EventEntity
        {
            Name = "Party",
            Date = new DateTimeOffset(2026, 10, 31, 20, 0, 0, TimeSpan.Zero),
            Selections = [new Selection { Game = new Game { Id = brawl.Id, Name = brawl.Name }, Activity = retired }],
            WinnerActivityId = retired.Id
        };
        api.Json(HttpMethod.Get, "/api/games", new[] { kart, brawl, puzzle })
           .Json(HttpMethod.Get, "/api/activities", new[] { grandPrix, timeTrial, stock })
           .Json(HttpMethod.Get, "/api/themes", new[] { spooky })
           .Json(HttpMethod.Get, "/api/holidays", new[] { halloween })
           .Json(HttpMethod.Get, "/api/events", new[] { night, party });
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    private static Selection Pick(GameEntity game, ActivityEntity act) => new()
    {
        Game = new Game { Id = game.Id, Name = game.Name },
        Activity = new Activity { Id = act.Id, GameId = act.GameId, Name = act.Name }
    };

    // ---- Delete asks first -------------------------------------------------------------------

    [TestMethod]
    [DataRow("events", "Delete event", "Game night")]
    [DataRow("games", "Delete game", "Kart")]
    [DataRow("activities", "Delete activity", "Grand Prix")]
    [DataRow("themes", "Delete theme", "Spooky")]
    [DataRow("holidays", "Delete holiday", "Halloween")]
    public async Task Delete_OpensConfirmationNamingItem_CancelMakesNoRequest(string route, string title, string item)
    {
        var page = Open(route);
        var rows = page.Rows();

        await RowButton(page, item, "Delete").ClickAsync(new());

        var confirmation = page.Confirmation();
        Assert.AreEqual(title, Heading(confirmation));
        Assert.AreEqual(title, confirmation.GetAttribute("aria-label"));
        Assert.Contains(item, Message(confirmation));
        Assert.IsFalse(confirmation.Button("Delete").IsBusy());

        await confirmation.Button("Cancel").ClickAsync(new());

        Assert.IsFalse(page.HasConfirmation());
        Assert.IsEmpty(api.Requests.Where(r => r.Method != HttpMethod.Get).ToList());
        CollectionAssert.AreEqual(rows, page.Rows());
        Assert.IsEmpty(page.PageAlerts());
    }

    [TestMethod]
    public async Task Delete_ConfirmRunsDelete_AndReloads()
    {
        var page = Open("themes");
        var path = $"/api/themes/{spooky.Id}";
        api.Status(HttpMethod.Delete, path, HttpStatusCode.NoContent)
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>());
        var reads = api.Count(HttpMethod.Get, "/api/themes");

        await RowButton(page, "Spooky", "Delete").ClickAsync(new());
        Assert.AreEqual(0, api.Count(HttpMethod.Delete, path));
        await page.ConfirmAsync();

        Assert.AreEqual(1, api.Count(HttpMethod.Delete, path));
        Assert.AreEqual(reads + 1, api.Count(HttpMethod.Get, "/api/themes"));
        // An empty grid still renders its empty-content row, so look for the deleted name instead.
        page.WaitForAssertion(() => Assert.IsFalse(page.Rows().Any(r => r.Contains("Spooky"))));
        Assert.IsFalse(page.HasConfirmation());
        Assert.IsEmpty(page.PageAlerts());
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(0)]
    public async Task GameDelete_ConfirmationShowsActivityCount(int count)
    {
        var game = count == 2 ? kart : puzzle;
        var page = Open("games");

        await RowButton(page, game.Name, "Delete").ClickAsync(new());

        Assert.AreEqual(count == 2
            ? "Kart has 2 activities. The server refuses to delete a game that activities still use, so delete or move them first."
            : "Delete Puzzle? This cannot be undone.", Message(page.Confirmation()));
        // The server decides: Delete is still offered, and its refusal shows in the page's error bar.
        if (count == 2)
        {
            api.Status(HttpMethod.Delete, $"/api/games/{kart.Id}", HttpStatusCode.Conflict, "This game is used by 2 activities. Delete or move those activities first.");
            await page.ConfirmAsync();
            Assert.AreEqual(1, api.Count(HttpMethod.Delete, $"/api/games/{kart.Id}"));
            Assert.StartsWith("The game was not deleted.", Assert.ContainsSingle(page.PageAlerts()));
            Assert.AreEqual(3, page.Rows().Count);
        }
    }

    // ---- One request per click ----------------------------------------------------------------

    [TestMethod]
    public async Task Duplicate_DoubleClick_SendsOneRequest()
    {
        var page = Open("activities");
        var path = $"/api/activities/{grandPrix.Id}/duplicate";
        var gate = api.Hold(HttpMethod.Post, path, () => new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(grandPrix) });

        var first = RowButton(page, "Grand Prix", "Duplicate").ClickAsync(new());

        // While it runs: this row's buttons and every row's Duplicate and Delete are disabled.
        Assert.IsTrue(RowButton(page, "Grand Prix", "Duplicate").IsBusy());
        Assert.IsTrue(RowButton(page, "Grand Prix", "Edit").IsBusy());
        Assert.IsTrue(page.FindAll("fluent-button[title=Duplicate]").All(b => b.IsBusy()));
        Assert.IsTrue(page.FindAll("fluent-button[title=Delete]").All(b => b.IsBusy()));
        await RowButton(page, "Grand Prix", "Duplicate").ClickAsync(new());
        // A click that lands before the re-render reaches the handler itself, which sends nothing.
        // Not awaited before the release: without the guard it would wait on the held request too.
        var second = page.InvokeAsync(() => ((RowAction)Field(page.Instance, "_rows")!).RunAsync(grandPrix.Id, () => (Task)Call(page.Instance, "Duplicate", grandPrix.Id)!));

        gate.SetResult();
        await first;
        await second;

        Assert.AreEqual(1, api.Count(HttpMethod.Post, path));
        page.WaitForAssertion(() => Assert.IsFalse(RowButton(page, "Grand Prix", "Duplicate").IsBusy()));
        Assert.IsEmpty(page.PageAlerts());
    }

    [TestMethod]
    public async Task SelectWinner_DisablesAllWinnerButtonsWhileRunning_AndSendsOneRequest()
    {
        var page = Open("events");
        await RowButton(page, "Game night", "Details").ClickAsync(new());
        var path = $"/api/events/{night.Id}";
        var gate = api.Hold(HttpMethod.Put, path, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(night) });
        Assert.HasCount(3, WinnerButtons(page));

        var first = WinnerButtons(page)[0].ClickAsync(new());

        var during = WinnerButtons(page);
        Assert.HasCount(2, during);
        Assert.IsTrue(during.All(b => b.IsBusy()));
        Assert.IsTrue(page.FormDialog().Button("Close").IsBusy());
        await during[0].ClickAsync(new());
        // A click that lands before the re-render reaches the handler itself, which sends nothing.
        // Not awaited before the release: without the guard it would wait on the held request too.
        var second = page.InvokeAsync(() => (Task)Call(page.Instance, "SelectWinner", stock.Id)!);

        gate.SetResult();
        await first;
        await second;

        Assert.AreEqual(1, api.Count(HttpMethod.Put, path));
        Assert.Contains($"\"winnerActivityId\":\"{grandPrix.Id}\"", Assert.ContainsSingle(api.Bodies(HttpMethod.Put, path))!);
        page.WaitForAssertion(() => Assert.IsTrue(WinnerButtons(page).All(b => !b.IsBusy())));
        Assert.IsFalse(page.FormDialog().Button("Close").IsBusy());
    }

    // ---- A late save leaves a newer dialog alone ------------------------------------------------
    // While a save runs the page itself never lets its dialog close (Cancel is disabled and
    // guarded, overlay click and Escape do nothing). These tests close it through the page's own
    // close method anyway, to prove the save's dialog token on its own.

    [TestMethod]
    public async Task SaveFinishingAfterCancelAndReopen_DoesNotCloseOrResetTheNewerDialog()
    {
        var page = Open("games");
        var pathA = $"/api/games/{kart.Id}";
        var gate = api.Hold(HttpMethod.Put, pathA, () => new HttpResponseMessage(HttpStatusCode.NoContent));
        await RowButton(page, "Kart", "Edit").ClickAsync(new());
        await TextField(page).ChangeAsync(new ChangeEventArgs { Value = "Kart renamed" });
        var save = page.FormDialog().QuerySelector("form")!.SubmitAsync();

        await CloseThrough(page, "CloseEdit");
        Assert.IsFalse(page.HasFormDialog());
        await RowButton(page, "Brawl", "Edit").ClickAsync(new());
        await TextField(page).ChangeAsync(new ChangeEventArgs { Value = "Brawl typed" });

        gate.SetResult();
        await save;

        Assert.AreEqual("Edit Game", page.DialogTitle());
        Assert.AreEqual("Brawl typed", TextField(page).GetAttribute("value"));
        Assert.AreEqual(brawl.Id, ((GameEntity)Field(page.Instance, "_edit")!).Id);
        Assert.Contains("Stock", page.FormDialog().TextContent);
        Assert.IsEmpty(page.FormDialog().Alerts());
        Assert.AreEqual(1, api.Count(HttpMethod.Put, pathA));
        Assert.Contains("Kart renamed", api.Bodies(HttpMethod.Put, pathA)[0]!);
        Assert.AreEqual(0, api.Count(HttpMethod.Put, $"/api/games/{brawl.Id}"));
        // Only the finishing save cleared the busy flag: B's Save is usable again.
        Assert.IsFalse(page.FormDialog().Button("Save Changes").IsBusy());
    }

    [TestMethod]
    public async Task EventsSaveFinishingAfterCancelAndReopen_DoesNotCloseOrResetTheNewerDialog()
    {
        var page = Open("events");
        var pathA = $"/api/events/{night.Id}";
        var gate = api.Hold(HttpMethod.Put, pathA, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(night) });
        await RowButton(page, "Game night", "Edit").ClickAsync(new());
        await EventName(page).ChangeAsync(new ChangeEventArgs { Value = "Night renamed" });
        var save = page.FormDialog().Button("Update Event").ClickAsync(new());
        Assert.IsTrue(page.FormDialog().Button("Cancel").IsBusy());

        await CloseThrough(page, "CloseCustomize");
        Assert.IsFalse(page.HasFormDialog());
        await RowButton(page, "Party", "Edit").ClickAsync(new());
        await EventName(page).ChangeAsync(new ChangeEventArgs { Value = "Party typed" });

        gate.SetResult();
        await save;

        Assert.AreEqual("Edit Event", page.DialogTitle());
        Assert.AreEqual("Party typed", EventName(page).GetAttribute("value"));
        Assert.AreEqual(party.Id, ((EventEntity)Field(page.Instance, "_editingEvent")!).Id);
        Assert.IsEmpty(page.FormDialog().Alerts());
        Assert.AreEqual(1, api.Count(HttpMethod.Put, pathA));
        Assert.Contains("Night renamed", api.Bodies(HttpMethod.Put, pathA)[0]!);
        Assert.AreEqual(0, api.Count(HttpMethod.Put, $"/api/events/{party.Id}"));
        Assert.IsFalse(page.FormDialog().Button("Update Event").IsBusy());
    }

    [TestMethod]
    public async Task CancelButton_IsIgnoredWhileSaveInFlight()
    {
        var page = Open("games");
        var path = $"/api/games/{kart.Id}";
        var gate = api.Hold(HttpMethod.Put, path, () => new HttpResponseMessage(HttpStatusCode.NoContent));
        await RowButton(page, "Kart", "Edit").ClickAsync(new());
        await TextField(page).ChangeAsync(new ChangeEventArgs { Value = "Kart renamed" });
        var save = page.FormDialog().QuerySelector("form")!.SubmitAsync();

        var cancel = page.FormDialog().Button("Cancel");
        Assert.IsTrue(cancel.IsBusy());
        await cancel.ClickAsync(new());
        // A click that lands before the re-render reaches the handler itself, which ignores it.
        await CloseThrough(page, "CancelEdit");

        Assert.IsTrue(page.HasFormDialog());
        Assert.AreEqual("Kart renamed", TextField(page).GetAttribute("value"));
        Assert.IsTrue(page.FormDialog().Button("Save Changes").IsBusy());

        gate.SetResult();
        await save;

        // The save that finished closes its own dialog.
        Assert.IsFalse(page.HasFormDialog());
        Assert.AreEqual(1, api.Count(HttpMethod.Put, path));
    }

    // ---- Overlay click and Escape -------------------------------------------------------------

    public static IEnumerable<object[]> ChangedForms =>
    [
        ["games-create"], ["games-edit"], ["themes-create"], ["themes-edit"], ["holidays-create"], ["holidays-edit"],
        ["activities-create"], ["activities-edit"], ["activities-details"],
        ["events-create"], ["events-edit"], ["events-edit-slots"], ["events-details"]
    ];

    public static IEnumerable<object[]> UntouchedForms =>
    [
        ["games-create"], ["games-edit"], ["themes-create"], ["themes-edit"], ["holidays-create"], ["holidays-edit"],
        ["activities-create"], ["activities-edit"], ["activities-details"],
        ["events-create"], ["events-edit"], ["events-edit-deleted-activity"], ["events-details"]
    ];

    [TestMethod]
    [DynamicData(nameof(ChangedForms))]
    public async Task Dismiss_WithUnsavedChanges_AsksToDiscard_KeepEditingKeepsDialog_DiscardCloses(string form)
    {
        var (page, value) = await OpenForm(form, change: true);
        var typed = value();

        await Dismiss(page);

        Assert.IsTrue(page.HasFormDialog());
        var confirmation = page.Confirmation();
        Assert.AreEqual("Discard changes?", Heading(confirmation));
        // The form dialog lets go of focus so the question can take it.
        Assert.AreEqual("false", page.FormDialog().GetAttribute("trap-focus"));
        await confirmation.Button("Keep editing").ClickAsync(new());
        Assert.IsFalse(page.HasConfirmation());
        Assert.IsTrue(page.HasFormDialog());
        Assert.AreEqual("true", page.FormDialog().GetAttribute("trap-focus"));
        Assert.AreEqual(typed, value());

        // Escape while the question is open reaches the form dialog first; it answers Keep editing.
        await Dismiss(page);
        Assert.IsTrue(page.HasConfirmation());
        await Dismiss(page);
        Assert.IsFalse(page.HasConfirmation());
        Assert.IsTrue(page.HasFormDialog());
        Assert.AreEqual(typed, value());

        await Dismiss(page);
        await page.ConfirmAsync("Discard");

        Assert.IsFalse(page.HasFormDialog());
        Assert.IsFalse(page.HasConfirmation());
        Assert.IsEmpty(api.Requests.Where(r => r.Method != HttpMethod.Get).ToList());
    }

    // Escape pressed while the cursor is still in a field: the browser has raised "input" for every
    // keystroke but no "change" yet (that only comes when the field loses focus). So these tests type
    // with InputAsync, never ChangeAsync, and the typed text must already count as a change.
    [TestMethod]
    [DataRow("games-create")]
    [DataRow("games-edit")]
    [DataRow("themes-create")]
    [DataRow("themes-edit")]
    [DataRow("holidays-create")]
    [DataRow("holidays-edit")]
    [DataRow("activities-create")]
    [DataRow("activities-edit")]
    [DataRow("activities-details")]
    [DataRow("events-create")]
    [DataRow("events-edit")]
    [DataRow("events-details")]
    public async Task Dismiss_RightAfterTyping_BeforeFieldLosesFocus_AsksToDiscard(string form)
    {
        var (page, _) = await OpenForm(form, change: false);
        const string typed = "Typed, cursor still in the field";

        await TextInputs(page)[0].InputAsync(new ChangeEventArgs { Value = typed });
        await Dismiss(page);

        Assert.IsTrue(page.HasFormDialog(), $"{form}: Escape right after typing closed the dialog and lost the text");
        Assert.IsTrue(page.HasConfirmation(), $"{form}: Escape right after typing did not ask");
        Assert.AreEqual("Discard changes?", Heading(page.Confirmation()));
        await page.Confirmation().Button("Keep editing").ClickAsync(new());
        Assert.IsTrue(page.HasFormDialog());
        Assert.AreEqual(typed, TextInputs(page)[0].GetAttribute("value"));

        await Dismiss(page);
        await page.ConfirmAsync("Discard");
        Assert.IsFalse(page.HasFormDialog());
        Assert.IsEmpty(api.Requests.Where(r => r.Method != HttpMethod.Get).ToList());
    }

    // Every text field of the dialogs with more than one counts while it is being typed in.
    [TestMethod]
    [DataRow("games-create", 2)]
    [DataRow("games-edit", 2)]
    [DataRow("activities-create", 4)]
    [DataRow("activities-edit", 4)]
    public async Task Dismiss_RightAfterTypingInAnyTextField_AsksToDiscard(string form, int fields)
    {
        for (var i = 0; i < fields; i++)
        {
            var (page, _) = await OpenForm(form, change: false);
            Assert.HasCount(fields, TextInputs(page));

            await TextInputs(page)[i].InputAsync(new ChangeEventArgs { Value = $"Typed in field {i}" });
            await Dismiss(page);

            Assert.IsTrue(page.HasConfirmation(), $"{form}: typing in text field {i} did not count as a change");
            await page.ConfirmAsync("Discard");
            Assert.IsFalse(page.HasFormDialog());
        }
        Assert.IsEmpty(api.Requests.Where(r => r.Method != HttpMethod.Get).ToList());
    }

    // The Event Time picker is typed into too; its input event must count before it loses focus.
    [TestMethod]
    [DataRow("events-create")]
    [DataRow("events-edit")]
    public async Task Dismiss_RightAfterTypingEventTime_AsksToDiscard(string form)
    {
        var (page, _) = await OpenForm(form, change: false);
        var time = page.FormDialog().QuerySelector("fluent-text-field.fluent-timepicker")!;
        var typed = time.GetAttribute("current-value") == "06:15" ? "06:45" : "06:15";

        await time.InputAsync(new ChangeEventArgs { Value = typed });
        await Dismiss(page);

        Assert.IsTrue(page.HasConfirmation(), $"{form}: Escape right after typing a time did not ask");
        await page.ConfirmAsync("Discard");
        Assert.IsFalse(page.HasFormDialog());
        Assert.IsEmpty(api.Requests.Where(r => r.Method != HttpMethod.Get).ToList());
    }

    [TestMethod]
    [DynamicData(nameof(UntouchedForms))]
    public async Task Dismiss_WithNoChanges_ClosesImmediately(string form)
    {
        var (page, _) = await OpenForm(form, change: false);

        await Dismiss(page);

        Assert.IsFalse(page.HasFormDialog());
        Assert.IsFalse(page.HasConfirmation());
        Assert.IsEmpty(api.Requests.Where(r => r.Method != HttpMethod.Get).ToList());
    }

    [TestMethod]
    [DataRow("games")]
    [DataRow("events")]
    public async Task Dismiss_DuringSave_DoesNothing(string route)
    {
        var (page, _) = await OpenForm(route == "games" ? "games-edit" : "events-edit", change: true);
        TaskCompletionSource gate;
        Task save;
        if (route == "games")
        {
            gate = api.Hold(HttpMethod.Put, $"/api/games/{kart.Id}", () => new HttpResponseMessage(HttpStatusCode.NoContent));
            save = page.FormDialog().QuerySelector("form")!.SubmitAsync();
        }
        else
        {
            gate = api.Hold(HttpMethod.Put, $"/api/events/{night.Id}", () => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(night) });
            save = page.FormDialog().Button("Update Event").ClickAsync(new());
        }

        await Dismiss(page);

        Assert.IsTrue(page.HasFormDialog());
        Assert.IsFalse(page.HasConfirmation());

        gate.SetResult();
        await save;
        Assert.IsFalse(page.HasFormDialog());
        Assert.IsFalse(page.HasConfirmation());
    }

    [TestMethod]
    public async Task ConfirmDialog_NamesQuestionAndButtons_DismissAnswersCancelOnce()
    {
        var confirmed = 0;
        var cancelled = 0;
        var cut = harness.Render<ConfirmDialog>(p => p
            .Add(c => c.Title, "Delete game")
            .Add(c => c.Message, "Delete Kart? This cannot be undone.")
            .Add(c => c.ConfirmText, "Delete")
            .Add(c => c.CancelText, "Cancel")
            .Add(c => c.Destructive, true)
            .Add(c => c.OnConfirm, () => confirmed++)
            .Add(c => c.OnCancel, () => cancelled++));
        var dialog = cut.Find("fluent-dialog");
        Assert.AreEqual("Delete game", dialog.GetAttribute("aria-label"));
        Assert.AreEqual("true", dialog.GetAttribute("modal"));
        Assert.AreEqual("Delete game", Heading(dialog));
        Assert.AreEqual("Delete Kart? This cannot be undone.", Message(dialog));
        Assert.IsNotNull(dialog.Button("Delete"));

        // Overlay click or Escape answers Cancel; a later click on Delete is ignored.
        await dialog.TriggerEventAsync("ondialogdismiss", new DialogEventArgs { Id = dialog.Id, Reason = "dismiss" });
        await cut.Find("fluent-dialog").Button("Delete").ClickAsync(new());

        Assert.AreEqual(1, cancelled);
        Assert.AreEqual(0, confirmed);
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private IRenderedComponent<IComponent> Open(string route)
    {
        IRenderedComponent<IComponent> page = route switch
        {
            "games" => harness.Render<Games>(),
            "activities" => harness.Render<Activities>(),
            "themes" => harness.Render<Themes>(),
            "holidays" => harness.Render<Holidays>(),
            _ => harness.Render<Events>()
        };
        page.WaitForRows(route switch { "games" or "activities" => 3, "events" => 2, _ => 1 });
        return page;
    }

    // Opens one form dialog, optionally changes one bound value, and returns how to read that value back.
    private async Task<(IRenderedComponent<IComponent> Page, Func<string?> Value)> OpenForm(string form, bool change)
    {
        var route = form[..form.IndexOf('-')];
        var page = Open(route);
        Func<string?> value = () => TextField(page).GetAttribute("value");
        IElement? field = null;
        switch (form)
        {
            case "games-create":
                await page.Button("Add Game").ClickAsync(new());
                break;
            case "games-edit":
                await RowButton(page, "Kart", "Edit").ClickAsync(new());
                break;
            case "themes-create":
                await page.Button("Add Theme").ClickAsync(new());
                break;
            case "holidays-create":
                await page.Button("Add Holiday").ClickAsync(new());
                break;
            case "themes-edit":
            case "holidays-edit":
                await page.Find("fluent-button[title=Edit]").ClickAsync(new());
                break;
            case "activities-create":
                await page.Button("Add Activity").ClickAsync(new());
                break;
            case "activities-edit":
                await RowButton(page, "Grand Prix", "Edit").ClickAsync(new());
                break;
            case "activities-details":
                await RowButton(page, "Grand Prix", "Details").ClickAsync(new());
                value = () => page.FormDialog().QuerySelector("fluent-text-area")!.GetAttribute("value");
                if (change) field = page.FormDialog().QuerySelector("fluent-text-area");
                break;
            case "events-create":
                await page.Button("Create Event").ClickAsync(new());
                value = () => EventName(page).GetAttribute("value");
                if (change) field = EventName(page);
                break;
            case "events-edit":
            case "events-edit-slots":
                await RowButton(page, "Game night", "Edit").ClickAsync(new());
                value = () => EventName(page).GetAttribute("value");
                if (change) field = EventName(page);
                break;
            case "events-edit-deleted-activity":
                await RowButton(page, "Party", "Edit").ClickAsync(new());
                Assert.Contains("(deleted) Retired", page.FormDialog().TextContent);
                break;
            case "events-details":
                await RowButton(page, "Party", "Details").ClickAsync(new());
                value = () => page.FormDialog().QuerySelector("fluent-text-area")!.GetAttribute("value");
                if (change) field = page.FormDialog().QuerySelector("fluent-text-area");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(form), form);
        }
        Assert.IsTrue(page.HasFormDialog());
        if (!change) return (page, value);

        if (form == "events-edit-slots")
        {
            value = () => page.FormDialog().QuerySelectorAll("fluent-button[aria-label^='Re-roll slot']").Length.ToString();
            await page.FormDialog().QuerySelectorAll("fluent-button[title='Remove slot']")[0].ClickAsync(new());
            Assert.AreEqual("2", value());
        }
        else
        {
            await (field ?? TextField(page)).ChangeAsync(new ChangeEventArgs { Value = "Typed but not saved" });
            Assert.AreEqual("Typed but not saved", value());
        }
        return (page, value);
    }

    // Overlay click or Escape on the page's form or details dialog.
    private static Task Dismiss(IRenderedComponent<IComponent> page)
    {
        var dialog = page.FormDialog();
        return dialog.TriggerEventAsync("ondialogdismiss", new DialogEventArgs { Id = dialog.Id, Reason = "dismiss" });
    }

    // Runs one of the page's close methods, then renders, as an event handler would.
    private static Task CloseThrough(IRenderedComponent<IComponent> page, string method) => page.InvokeAsync(() =>
    {
        Call(page.Instance, method);
        Call(page.Instance, "StateHasChanged");
    });

    private static IElement RowButton(IRenderedComponent<IComponent> page, string item, string title) =>
        page.FindAll("tbody tr.fluent-data-grid-row")
            .Where(r => r.Closest("fluent-dialog") is null)
            .Single(r => r.TextContent.Contains(item))
            .QuerySelector($"fluent-button[title={title}]")!;

    private static List<IElement> WinnerButtons(IRenderedComponent<IComponent> page) =>
        page.FormDialog().QuerySelectorAll("fluent-button").Where(b => b.TextContent.Trim() == "Select Winner").ToList();

    private static IElement TextField(IRenderedComponent<IComponent> page) =>
        page.FormDialog().QuerySelector("fluent-text-field")!;

    // The dialog's free-text fields in page order. The date and time pickers also render as
    // fluent-text-field, but they are not typed into as free text, so they are left out.
    private static List<IElement> TextInputs(IRenderedComponent<IComponent> page) =>
        page.FormDialog().QuerySelectorAll("fluent-text-field:not(.fluent-datepicker):not(.fluent-timepicker), fluent-text-area").ToList();

    private static IElement EventName(IRenderedComponent<IComponent> page) =>
        page.FormDialog().QuerySelector("fluent-text-field[placeholder='Enter event name...']")!;

    private static string Heading(IElement dialog) => dialog.QuerySelector("h4")!.TextContent.Trim();

    private static string Message(IElement dialog) => dialog.QuerySelector(".confirm-message")!.TextContent.Trim();

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static object? Call(object page, string name, params object[] args) =>
        page.GetType().GetMethod(name, Private)!.Invoke(page, args);

    private static object? Field(object page, string name) =>
        page.GetType().GetField(name, Private)!.GetValue(page);
}
