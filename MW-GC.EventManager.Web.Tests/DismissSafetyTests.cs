using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// Issue #52 on all five pages: overlay click or Escape asks before discarding typed changes, closes a clean dialog
// at once, and does nothing while a save runs.
[TestClass]
public sealed class DismissSafetyTests : DialogSafetyFixture
{
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
        // The save may still be queued behind other renderer work, so wait for its render.
        page.WaitForAssertion(() => Assert.IsTrue(page.FormDialog().Button("Cancel").IsBusy()));

        await Dismiss(page);

        Assert.IsTrue(page.HasFormDialog());
        Assert.IsFalse(page.HasConfirmation());

        gate.SetResult();
        await save;
        Assert.IsFalse(page.HasFormDialog());
        Assert.IsFalse(page.HasConfirmation());
    }

    // #186 item 3: an Event whose Winner is no longer in the library. The API answers 404 for that Activity's
    // Comments, so the details dialog shows the load error and no text box, and has nothing to discard.
    [TestMethod]
    public async Task EventDetails_ARetiredWinner_ShowsTheCommentsLoadError()
    {
        var old = new EventEntity
        {
            Name = "Old party",
            Date = new DateTimeOffset(2025, 10, 31, 20, 0, 0, TimeSpan.Zero),
            Selections = [new Selection { Game = new Game { Id = brawl.Id, Name = brawl.Name }, Activity = retired }],
            WinnerActivityId = retired.Id
        };
        api.Json(HttpMethod.Get, "/api/events", new[] { night, old });
        var page = Open("events");

        await RowButton(page, "Old party", "Details").ClickAsync(new());

        var panel = () => page.FormDialog().QuerySelector(".comments")!;
        page.WaitForAssertion(() => Assert.Contains("Comments could not be loaded. Not found. It may have been deleted.", panel().TextContent));
        Assert.AreEqual(1, api.Count(HttpMethod.Get, $"/api/activities/{retired.Id}/comments"));
        Assert.IsEmpty(page.FormDialog().QuerySelectorAll("fluent-text-area").ToList());
        Assert.IsNotNull(panel().Button("Retry"));

        await Dismiss(page);
        Assert.IsFalse(page.HasFormDialog());
        Assert.IsFalse(page.HasConfirmation());
        Assert.IsEmpty(api.Requests.Where(r => r.Method != HttpMethod.Get).ToList());
    }
}
