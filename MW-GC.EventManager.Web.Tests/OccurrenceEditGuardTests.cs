using System.Net;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;

namespace MW_GC.EventManager.Web.Tests;

// #100, #204 (Web part 2b, stories 21, 22, 25): the edit scope question's guards. Cancel, Escape and overlay click send
// nothing and leave the edit dialog open with its values and focus back in it; a refusal (a stale eTag's 409, a 400, a
// failed read) shows the API's words in the dialog and keeps it open; a double click sends once; a success closes the
// dialog and reads the list again; the question works by keyboard. Fixture: OccurrenceEditFixture.
[TestClass]
public sealed class OccurrenceEditGuardTests : OccurrenceEditFixture
{
    private const string Stale = "The request conflicts with the stored data. Reload and try again.";
    private const string SplitAgain = "This Series was already split by a this and following edit at another Occurrence. Edit the Series that follows it, or this one with scope all.";
    private const string ReadAgain = "Close this dialog and edit the event again to read its series.";
    private const string NoVersion = "The series was read without its version, so saving it could overwrite another admin's change. Close this dialog and edit the event again.";
    private const string OtherTag = "W/\"datetime'2026-11-25T11%3A30%3A00.0000000Z'\"";

    [TestMethod]
    [DataRow("cancel")]
    [DataRow("escape on the question")]
    [DataRow("escape on the edit dialog")]
    public async Task CancelOrEscape_SendsNothing_AndKeepsTheEditDialogOpen_WithItsValues_AndFocusInIt(string how)
    {
        var page = await OpenEdit("Weekly raid");
        await Rename(page, "Raid night");
        await Update(page);
        await Choose(page, "All future events");
        Assert.AreEqual("false", EditDialog(page).GetAttribute("trap-focus"), "the edit dialog holds focus under the question");

        if (how == "cancel") await ScopeQuestion(page).Button("Cancel").ClickAsync(new());
        else if (how == "escape on the question") await ScopeQuestion(page).TriggerEventAsync("ondialogdismiss", new DialogEventArgs { Id = ScopeQuestion(page).Id, Reason = "dismiss" });
        else await EditDialog(page).TriggerEventAsync("ondialogdismiss", new DialogEventArgs { Id = EditDialog(page).Id, Reason = "dismiss" });

        Assert.IsFalse(HasScopeQuestion(page));
        Assert.IsFalse(page.HasConfirmation(), "a Discard changes? question opened");
        Assert.IsTrue(page.HasFormDialog(), "the edit dialog closed");
        Assert.AreEqual("Raid night", EditDialog(page).QuerySelector("fluent-text-field[placeholder='Enter event name...']")!.GetAttribute("value"));
        Assert.AreEqual("true", EditDialog(page).GetAttribute("trap-focus"), "the edit dialog does not take focus back");
        Assert.IsEmpty(Sent());
        // Focus goes back into the open edit dialog (emFocus.answered), not to the row's Edit button (emFocus.restore).
        JSInterop.VerifyInvoke("emFocus.question", 1);
        JSInterop.VerifyInvoke("emFocus.answered", 1);
        JSInterop.VerifyNotInvoke("emFocus.restore");
        Assert.IsEmpty(EditDialog(page).Alerts());
    }

    [TestMethod]
    [DataRow("This and following events", HttpStatusCode.Conflict, Stale)]
    [DataRow("All future events", HttpStatusCode.Conflict, Stale)]
    [DataRow("This and following events", HttpStatusCode.BadRequest, SplitAgain)]
    [DataRow("All future events", HttpStatusCode.BadRequest, "This Series has an end. Send its stored end to keep it, or a new one: an edit cannot remove it.")]
    public async Task ARefusedSeriesPut_ShowsTheApisWords_InTheDialog_AndKeepsItOpen(string option, HttpStatusCode status, string message)
    {
        api.Status(HttpMethod.Put, SeriesPath(RaidSeries), status, message);
        var page = await OpenEdit("Weekly raid");
        await Rename(page, "Raid night");
        await Update(page);

        await ChooseAndConfirm(page, option);

        page.WaitForAssertion(() => Assert.AreEqual($"The event was not saved. {message}", Assert.ContainsSingle(EditDialog(page).Alerts())));
        Assert.IsFalse(HasScopeQuestion(page));
        Assert.IsFalse(page.FormDialog().Button("Update Event").IsBusy(), "the dialog stayed busy after the refusal");
        var query = option == "All future events" ? "scope=all" : $"scope=following&occurrence={raid.Id:D}";
        CollectionAssert.AreEqual(new[] { $"PUT {SeriesPath(RaidSeries)}?{query}" }, Sent(), "the refused save sent more than its one Series PUT");
        Assert.AreEqual(0, api.Count(HttpMethod.Put, EventPath(raid)), "an Event PUT followed the refused Series PUT");
        Assert.IsTrue(page.HasFormDialog(), "the edit dialog closed after the refusal");
        Assert.IsEmpty(page.PageAlerts());
    }

    // The Series is read once, as the edit dialog opens; a failed read is never repeated at Confirm (that read would take
    // a version newer than the one the admin opened), so the message says to open the dialog again.
    [TestMethod]
    [DataRow(HttpStatusCode.NotFound, "Not found. It may have been deleted.")]
    [DataRow(HttpStatusCode.InternalServerError, "The server had a problem (HTTP 500). Try again.")]
    public async Task AFailedSeriesRead_ShowsItsMessage_AndSendsNoPut(HttpStatusCode status, string message)
    {
        api.Status(HttpMethod.Get, SeriesPath(RaidSeries), status);
        var page = await OpenEdit("Weekly raid");
        await Rename(page, "Raid night");
        await Update(page);

        await ChooseAndConfirm(page, "All future events");

        page.WaitForAssertion(() => Assert.AreEqual($"The event was not saved. {message} {ReadAgain}", Assert.ContainsSingle(EditDialog(page).Alerts())));
        CollectionAssert.AreEqual(new[] { $"GET {SeriesPath(RaidSeries)}" }, OpenReads());
        Assert.IsEmpty(Sent());
    }

    // Only a series scope needs the Series: a failed read leaves This event only and an unchanged save (which sends
    // nothing, #237 S1) as they were.
    [TestMethod]
    [DataRow("This event only")]
    [DataRow("no change")]
    public async Task AFailedSeriesRead_StillSavesThisEventOnly_AndClosesAnUnchangedOccurrence(string how)
    {
        api.Status(HttpMethod.Get, SeriesPath(RaidSeries), HttpStatusCode.InternalServerError);
        var page = await OpenEdit("Weekly raid");
        if (how == "This event only")
        {
            await Rename(page, "Raid night");
            await Update(page);
            await ChooseAndConfirm(page, how);
        }
        else await Update(page);

        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        CollectionAssert.AreEqual(how == "no change" ? Array.Empty<string>() : new[] { $"PUT {EventPath(raid)}" }, Sent());
        Assert.IsEmpty(page.PageAlerts());
    }

    // Story 22: another admin saves the Series after this edit dialog opened and before Confirm. The series scope sends
    // the eTag read when the dialog opened, so the API refuses it (409) instead of this admin silently overwriting the
    // other's change. The dialog stays open with the API's words; no Event PUT and no list reload follow.
    [TestMethod]
    [DataRow("This and following events")]
    [DataRow("All future events")]
    public async Task ASeriesSavedByAnotherAdmin_AfterTheDialogOpened_IsRefused_NotOverwritten(string option)
    {
        // The API as it behaves: a Series PUT whose If-Match is not the version stored now is a 409.
        var current = RaidTag;
        api.On(HttpMethod.Put, SeriesPath(RaidSeries), () => api.IfMatches(HttpMethod.Put, SeriesPath(RaidSeries))[^1] == current
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }
            : new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent(Stale) });
        var page = await OpenEdit("Weekly raid");
        var reads = api.Count(HttpMethod.Get, "/api/events");

        // Another admin renames the Series: it now reads back with that name and a newer eTag.
        var other = JsonSerializer.Deserialize<SeriesEntity>(JsonSerializer.Serialize(raidSeries, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!;
        other.Name = "Raid by another admin";
        current = OtherTag;
        api.On(HttpMethod.Get, SeriesPath(RaidSeries), () => Stored(other, OtherTag));

        await Rename(page, "Raid night");
        await Update(page);
        await ChooseAndConfirm(page, option);

        page.WaitForAssertion(() => Assert.AreEqual(1, api.Count(HttpMethod.Put, SeriesPath(RaidSeries))));
        CollectionAssert.AreEqual(new[] { RaidTag }, api.IfMatches(HttpMethod.Put, SeriesPath(RaidSeries)), "the edit was sent under a version read after the dialog opened");
        page.WaitForAssertion(() => Assert.AreEqual($"The event was not saved. {Stale}", Assert.ContainsSingle(EditDialog(page).Alerts())));
        var query = option == "All future events" ? "scope=all" : $"scope=following&occurrence={raid.Id:D}";
        CollectionAssert.AreEqual(new[] { $"PUT {SeriesPath(RaidSeries)}?{query}" }, Sent());
        Assert.AreEqual(0, api.Count(HttpMethod.Put, EventPath(raid)), "an Event PUT followed the refused Series PUT");
        Assert.IsTrue(page.HasFormDialog(), "the edit dialog closed after the refusal");
        Assert.IsFalse(HasScopeQuestion(page));
        Assert.AreEqual(reads, api.Count(HttpMethod.Get, "/api/events"), "the list was read again as after a save");
        CollectionAssert.AreEqual(new[] { $"GET {SeriesPath(RaidSeries)}" }, OpenReads(), "opening the edit dialog did not read the Series");
    }

    // A Series read without its version could only be written back unconditionally: no PUT is sent, and the dialog says why.
    [TestMethod]
    [DataRow("This and following events")]
    [DataRow("All future events")]
    public async Task ASeriesReadWithoutAnETag_SendsNoPut_AndSaysWhy(string option)
    {
        api.Json(HttpMethod.Get, SeriesPath(RaidSeries), raidSeries);
        var page = await OpenEdit("Weekly raid");
        await Rename(page, "Raid night");
        await Update(page);

        await ChooseAndConfirm(page, option);

        page.WaitForAssertion(() => Assert.AreEqual($"The event was not saved. {NoVersion}", Assert.ContainsSingle(EditDialog(page).Alerts())));
        Assert.IsEmpty(Sent());
        Assert.AreEqual(0, api.Count(HttpMethod.Put, SeriesPath(RaidSeries)));
        Assert.IsTrue(page.HasFormDialog(), "the edit dialog closed");
        Assert.IsFalse(page.FormDialog().Button("Update Event").IsBusy(), "the dialog stayed busy");
    }

    [TestMethod]
    public async Task ARefusedEventPut_ForThisEventOnly_ShowsTheApisWords_InTheDialog()
    {
        api.Status(HttpMethod.Put, EventPath(raid), HttpStatusCode.Conflict, Stale);
        var page = await OpenEdit("Weekly raid");
        await Rename(page, "Raid night");
        await Update(page);

        await ChooseAndConfirm(page, "This event only");

        page.WaitForAssertion(() => Assert.AreEqual($"The event was not saved. {Stale}", Assert.ContainsSingle(EditDialog(page).Alerts())));
        CollectionAssert.AreEqual(new[] { $"PUT {EventPath(raid)}" }, Sent());
    }

    // The Occurrence is 15 October at the Series' UTC-04:00 (16 October 04:45 in the browser's Kathmandu). 11:00 on 16
    // October in Kathmandu is 01:15 on 16 October at UTC-04:00: another day there, so a series scope would move every
    // Occurrence to a time the admin did not pick. Only a Series read can tell (its offset), so it is refused after it.
    [TestMethod]
    public async Task ATimeOnAnotherDayAtTheSeriesOffset_IsRefusedInTheDialog_AndSendsNoPut()
    {
        var page = await OpenEdit("Weekly raid");
        await TypeTime(page, "11:00");
        await Update(page);

        await ChooseAndConfirm(page, "All future events");

        page.WaitForAssertion(() => Assert.AreEqual(
            "The event was not saved. At the series' own UTC offset this time falls on another day, so only this event can take it. Choose This event only, or another time.",
            Assert.ContainsSingle(EditDialog(page).Alerts())));
        CollectionAssert.AreEqual(new[] { $"GET {SeriesPath(RaidSeries)}" }, OpenReads());
        Assert.IsEmpty(Sent());
    }

    [TestMethod]
    [DataRow("This event only")]
    [DataRow("This and following events")]
    [DataRow("All future events")]
    public async Task ADoubleClick_SendsOnce_AndTheDialogIsBusyWhileItRuns(string option)
    {
        var path = option == "This event only" ? EventPath(raid) : SeriesPath(RaidSeries);
        var page = await OpenEdit("Weekly raid");
        await Rename(page, "Raid night");
        await Update(page);
        // Held only once the question is up, so a save that skips the question fails here instead of hanging.
        var gate = api.Hold(HttpMethod.Put, path, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        await Choose(page, option);
        // Confirm and Update Event, both taken while enabled: clicks that land before the re-render that disables them.
        var confirm = Handler(page, b => b.ClassList.Contains("scope-confirm"));

        var first = page.InvokeAsync(() => confirm.InvokeAsync(new()));
        page.WaitForAssertion(() => Assert.IsTrue(page.FormDialog().Button("Update Event").IsBusy(), "the dialog is not busy while the save runs"));
        var update = Handler(page, b => b.TextContent.Trim() == "Update Event");
        await page.InvokeAsync(() => confirm.InvokeAsync(new()));
        await page.InvokeAsync(() => update.InvokeAsync(new()));
        Assert.IsFalse(HasScopeQuestion(page), "a second question opened while the save ran");

        gate.SetResult();
        await first;
        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        Assert.AreEqual(1, api.Count(HttpMethod.Put, path), "the save was sent twice");
    }

    [TestMethod]
    [DataRow("This event only")]
    [DataRow("This and following events")]
    [DataRow("All future events")]
    public async Task ASuccess_ClosesTheDialog_AndReadsTheListAgain(string option)
    {
        var page = await OpenEdit("Weekly raid");
        var reads = api.Count(HttpMethod.Get, "/api/events");
        await Rename(page, "Raid night");
        await Update(page);

        await ChooseAndConfirm(page, option);

        page.WaitForAssertion(() =>
        {
            Assert.IsFalse(page.HasFormDialog());
            Assert.AreEqual(reads + 1, api.Count(HttpMethod.Get, "/api/events"), "the list was not read again");
        });
        Assert.IsEmpty(page.PageAlerts());
        // The edit dialog closed: focus goes back to the row's Edit button, as after any save.
        JSInterop.VerifyInvoke("emFocus.restore", 1);
    }

    // Keyboard: the scopes are one radio group (Tab enters it once, the arrow keys move the choice, which the group
    // reports as a change of its value), Confirm is a fluent-button (Enter on it is the browser's click), and every
    // control has an accessible name.
    [TestMethod]
    public async Task TheQuestion_WorksByKeyboard_AndEveryControlHasAName()
    {
        var page = await OpenEdit("Monthly cup");
        await Rename(page, "Cup finals");
        await Update(page);

        var group = ScopeQuestion(page).QuerySelector("fluent-radio-group")!;
        Assert.AreEqual("What to update", group.GetAttribute("aria-label"));
        var radios = group.QuerySelectorAll("fluent-radio").ToList();
        CollectionAssert.AreEqual(new[] { "This event only", "This and following events", "All future events" }, radios.Select(r => r.GetAttribute("aria-label")).ToList());
        // One shared, non-empty name for the group and every radio: the browser's arrow keys move only between radios
        // of one name, and Tab enters and leaves the group once.
        var name = group.GetAttribute("name");
        Assert.IsFalse(string.IsNullOrEmpty(name), "the radio group has no name");
        CollectionAssert.AreEqual(new[] { name, name, name }, radios.Select(r => r.GetAttribute("name")).ToList());
        Assert.AreEqual("Cancel", ScopeQuestion(page).QuerySelectorAll("fluent-button").First().TextContent.Trim());

        // ArrowDown twice from the first radio, then back up once.
        await group.TriggerEventAsync("onradiogroupchange", new ChangeEventArgs { Value = radios[2].GetAttribute("current-value") });
        Assert.AreEqual("Update all future events", ConfirmButton(page).TextContent.Trim());
        await group.TriggerEventAsync("onradiogroupchange", new ChangeEventArgs { Value = radios[1].GetAttribute("current-value") });
        Assert.AreEqual("Update this and following", ConfirmButton(page).TextContent.Trim());
        await ConfirmButton(page).ClickAsync(new());

        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        CollectionAssert.AreEqual(new[] { $"GET {SeriesPath(CupSeries)}" }, OpenReads());
        CollectionAssert.AreEqual(new[] { $"PUT {SeriesPath(CupSeries)}?scope=following&occurrence={cup.Id:D}" }, Sent());
    }
}
