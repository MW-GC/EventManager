using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Shared;
using MW_GC.EventManager.Web.Shared.Events;
using MW_GC.EventManager.Web.Shared.Library;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// Issue #52 on all five pages: every Delete asks first, row actions and Select Winner run once, and a late save
// never closes a newer dialog. Overlay click and Escape are in DismissSafetyTests.
[TestClass]
public sealed class DialogSafetyTests : DialogSafetyFixture
{
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
        // The row's Duplicate button handler, as the page wired it, taken while the button is still enabled.
        var duplicate = page.FindComponents<FluentButton>()
            .Single(b => b.Instance.AdditionalAttributes?.TryGetValue("aria-label", out var label) == true && Equals(label, "Duplicate Grand Prix"))
            .Instance.OnClick;

        var first = RowButton(page, "Grand Prix", "Duplicate").ClickAsync(new());

        // While it runs: this row's buttons and every row's Duplicate and Delete are disabled.
        // The click may still be queued behind other renderer work, so wait for its render.
        page.WaitForAssertion(() =>
        {
            Assert.IsTrue(RowButton(page, "Grand Prix", "Duplicate").IsBusy());
            Assert.IsTrue(RowButton(page, "Grand Prix", "Edit").IsBusy());
            Assert.IsTrue(page.FindAll("fluent-button[title=Duplicate]").All(b => b.IsBusy()));
            Assert.IsTrue(page.FindAll("fluent-button[title=Delete]").All(b => b.IsBusy()));
        });
        await RowButton(page, "Grand Prix", "Duplicate").ClickAsync(new());
        // A click that lands before the re-render reaches the handler itself, which sends nothing: run the
        // button's own handler again, past its disabled state. Not awaited before the release: without the
        // guard it would wait on the held request too. The guard lives in the shared Library page (#126).
        var second = page.InvokeAsync(() => duplicate.InvokeAsync(new()));

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
        // The Select Winner handlers as the dialog wired them (Grand Prix, Stock, Time Trial), taken while enabled.
        var handlers = ClickHandlers(page, "Select Winner");
        Assert.HasCount(3, handlers);

        var first = WinnerButtons(page)[0].ClickAsync(new());

        // The click may still be queued behind other renderer work, so wait for its render.
        page.WaitForAssertion(() =>
        {
            var busy = WinnerButtons(page);
            Assert.HasCount(2, busy);
            Assert.IsTrue(busy.All(b => b.IsBusy()));
            Assert.IsTrue(page.FormDialog().Button("Close").IsBusy());
        });
        var during = WinnerButtons(page);
        await during[0].ClickAsync(new());
        // A click on Stock's button that lands before the re-render reaches the handler itself, which sends nothing.
        // Not awaited before the release: without the guard it would wait on the held request too.
        var second = page.InvokeAsync(() => handlers[1].InvokeAsync(new()));

        gate.SetResult();
        await first;
        await second;

        Assert.AreEqual(1, api.Count(HttpMethod.Put, path));
        Assert.Contains($"\"winnerActivityId\":\"{grandPrix.Id}\"", Assert.ContainsSingle(api.Bodies(HttpMethod.Put, path))!);
        page.WaitForAssertion(() => Assert.IsTrue(WinnerButtons(page).All(b => !b.IsBusy())));
        Assert.IsFalse(page.FormDialog().Button("Close").IsBusy());
    }

    // ---- A late save leaves a newer dialog alone ------------------------------------------------
    // While a save runs the page itself never lets its dialog close (Cancel is disabled and guarded, overlay
    // click and Escape do nothing), so no page event can close it then. These two tests close it through the
    // page's own close method anyway, to prove the save's dialog token on its own (#196 item 7: kept, since no
    // page event reaches it). What the newer dialog holds is read through the page: its fields, and where its
    // own save goes.

    [TestMethod]
    public async Task SaveFinishingAfterCancelAndReopen_DoesNotCloseOrResetTheNewerDialog()
    {
        var page = Open("games");
        var pathA = $"/api/games/{kart.Id}";
        var pathB = $"/api/games/{brawl.Id}";
        var gate = api.Hold(HttpMethod.Put, pathA, () => new HttpResponseMessage(HttpStatusCode.NoContent));
        api.Status(HttpMethod.Put, pathB, HttpStatusCode.NoContent);
        await RowButton(page, "Kart", "Edit").ClickAsync(new());
        await TextField(page).ChangeAsync(new ChangeEventArgs { Value = "Kart renamed" });
        var save = page.FormDialog().QuerySelector("form")!.SubmitAsync();
        // The submit may still be queued behind other renderer work, so wait for the save's render.
        page.WaitForAssertion(() => Assert.IsTrue(page.FormDialog().Button("Cancel").IsBusy()));

        await CloseThrough(page, page.FindComponent<LibraryPage<GameEntity>>().Instance, "CloseForm");
        Assert.IsFalse(page.HasFormDialog());
        await RowButton(page, "Brawl", "Edit").ClickAsync(new());
        await TextField(page).ChangeAsync(new ChangeEventArgs { Value = "Brawl typed" });

        gate.SetResult();
        await save;

        Assert.AreEqual("Edit Game", page.DialogTitle());
        Assert.AreEqual("Brawl typed", TextField(page).GetAttribute("value"));
        Assert.Contains("Stock", page.FormDialog().TextContent);
        Assert.IsEmpty(page.FormDialog().Alerts());
        Assert.AreEqual(1, api.Count(HttpMethod.Put, pathA));
        Assert.Contains("Kart renamed", api.Bodies(HttpMethod.Put, pathA)[0]!);
        Assert.AreEqual(0, api.Count(HttpMethod.Put, pathB));
        // Only the finishing save cleared the busy flag: B's Save is usable again, and it saves Brawl.
        Assert.IsFalse(page.FormDialog().Button("Save Changes").IsBusy());
        await page.FormDialog().QuerySelector("form")!.SubmitAsync();
        Assert.Contains("Brawl typed", Assert.ContainsSingle(api.Bodies(HttpMethod.Put, pathB))!);
        Assert.AreEqual(1, api.Count(HttpMethod.Put, pathA));
    }

    [TestMethod]
    public async Task EventsSaveFinishingAfterCancelAndReopen_DoesNotCloseOrResetTheNewerDialog()
    {
        var page = Open("events");
        var pathA = $"/api/events/{night.Id}";
        var pathB = $"/api/events/{party.Id}";
        var gate = api.Hold(HttpMethod.Put, pathA, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(night) });
        api.Json(HttpMethod.Put, pathB, party);
        await RowButton(page, "Game night", "Edit").ClickAsync(new());
        await EventName(page).ChangeAsync(new ChangeEventArgs { Value = "Night renamed" });
        var save = page.FormDialog().Button("Update Event").ClickAsync(new());
        // The click may still be queued behind other renderer work, so wait for the save's render.
        page.WaitForAssertion(() => Assert.IsTrue(page.FormDialog().Button("Cancel").IsBusy()));

        await CloseThrough(page, page.FindComponent<EventEditDialog>().Instance, "CloseCustomize");
        Assert.IsFalse(page.HasFormDialog());
        await RowButton(page, "Party", "Edit").ClickAsync(new());
        await EventName(page).ChangeAsync(new ChangeEventArgs { Value = "Party typed" });

        gate.SetResult();
        await save;

        Assert.AreEqual("Edit Event", page.DialogTitle());
        Assert.AreEqual("Party typed", EventName(page).GetAttribute("value"));
        Assert.IsEmpty(page.FormDialog().Alerts());
        Assert.AreEqual(1, api.Count(HttpMethod.Put, pathA));
        Assert.Contains("Night renamed", api.Bodies(HttpMethod.Put, pathA)[0]!);
        Assert.AreEqual(0, api.Count(HttpMethod.Put, pathB));
        // B's Update Event is usable again, and it saves Party.
        Assert.IsFalse(page.FormDialog().Button("Update Event").IsBusy());
        await page.FormDialog().Button("Update Event").ClickAsync(new());
        page.WaitForAssertion(() => Assert.AreEqual(1, api.Count(HttpMethod.Put, pathB)));
        Assert.Contains("Party typed", api.Bodies(HttpMethod.Put, pathB)[0]!);
        Assert.AreEqual(1, api.Count(HttpMethod.Put, pathA));
    }

    [TestMethod]
    public async Task CancelButton_IsIgnoredWhileSaveInFlight()
    {
        var page = Open("games");
        var path = $"/api/games/{kart.Id}";
        var gate = api.Hold(HttpMethod.Put, path, () => new HttpResponseMessage(HttpStatusCode.NoContent));
        await RowButton(page, "Kart", "Edit").ClickAsync(new());
        await TextField(page).ChangeAsync(new ChangeEventArgs { Value = "Kart renamed" });
        // Cancel's handler as the page wired it, taken while the button is enabled.
        var cancelHandler = ClickHandler(page, "Cancel");
        var save = page.FormDialog().QuerySelector("form")!.SubmitAsync();

        // The submit may still be queued behind other renderer work, so wait for the save's render.
        page.WaitForAssertion(() => Assert.IsTrue(page.FormDialog().Button("Cancel").IsBusy()));
        await page.FormDialog().Button("Cancel").ClickAsync(new());
        // A click that lands before the re-render reaches Cancel's handler itself, which ignores it.
        await page.InvokeAsync(() => cancelHandler.InvokeAsync(new()));

        Assert.IsTrue(page.HasFormDialog());
        Assert.AreEqual("Kart renamed", TextField(page).GetAttribute("value"));
        Assert.IsTrue(page.FormDialog().Button("Save Changes").IsBusy());

        gate.SetResult();
        await save;

        // The save that finished closes its own dialog.
        Assert.IsFalse(page.HasFormDialog());
        Assert.AreEqual(1, api.Count(HttpMethod.Put, path));
    }

    // ---- The question itself ------------------------------------------------------------------

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
        Assert.IsFalse(dialog.Button("Delete").IsBusy(), "a question's confirm button is usable unless it says otherwise");

        // Overlay click or Escape answers Cancel; a later click on Delete is ignored.
        await dialog.TriggerEventAsync("ondialogdismiss", new DialogEventArgs { Id = dialog.Id, Reason = "dismiss" });
        await cut.Find("fluent-dialog").Button("Delete").ClickAsync(new());

        Assert.AreEqual(1, cancelled);
        Assert.AreEqual(0, confirmed);
    }

    // #195 item 5: a question whose confirm must wait (ConfirmDisabled) shows it disabled and ignores it, even a
    // click that reaches the button's handler, without using up the answer: once enabled, it confirms once.
    [TestMethod]
    public async Task ConfirmDialog_AConfirmThatMustWait_IsDisabledAndIgnored_ThenAnswersOnce()
    {
        var confirmed = 0;
        var cancelled = 0;
        var cut = harness.Render<ConfirmDialog>(p => p
            .Add(c => c.Title, "Discard changes?")
            .Add(c => c.ConfirmText, "Discard")
            .Add(c => c.CancelText, "Keep editing")
            .Add(c => c.ConfirmDisabled, true)
            .Add(c => c.OnConfirm, () => confirmed++)
            .Add(c => c.OnCancel, () => cancelled++));
        var discard = cut.FindComponents<FluentButton>().Single(b => b.Find("fluent-button").TextContent.Trim() == "Discard").Instance.OnClick;

        Assert.IsTrue(cut.Find("fluent-dialog").Button("Discard").IsBusy());
        Assert.AreEqual("Discard", cut.Find("fluent-dialog").Button("Discard").TextContent.Trim());
        await cut.InvokeAsync(() => discard.InvokeAsync(new()));
        Assert.AreEqual(0, confirmed);

        cut.Render(p => p.Add(c => c.ConfirmDisabled, false));
        Assert.IsFalse(cut.Find("fluent-dialog").Button("Discard").IsBusy());
        await cut.Find("fluent-dialog").Button("Discard").ClickAsync(new());
        await cut.Find("fluent-dialog").Button("Discard").ClickAsync(new());

        Assert.AreEqual(1, confirmed);
        Assert.AreEqual(0, cancelled);
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static List<IElement> WinnerButtons(IRenderedComponent<IComponent> page) =>
        page.FormDialog().QuerySelectorAll("fluent-button").Where(b => b.TextContent.Trim() == "Select Winner").ToList();

    // Runs a close method of a component inside the page (the shared Library page that holds the Games dialogs,
    // or the Event edit dialog), then renders, as an event handler would. No page event can reach it during a save.
    private static Task CloseThrough(IRenderedComponent<IComponent> page, IComponent component, string method) => page.InvokeAsync(() =>
    {
        component.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, []);
        component.GetType().GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, []);
    });
}
