using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Tests;

// Issue #54: every dialog on the five pages returns focus to its opener (through FocusReturn's emFocus.remember /
// emFocus.restore), whether it closes by its button, by a dismiss, after a discard, a delete or a save. The page
// and its dialogs are opened through AccessibleNamesFixture.
[TestClass]
public sealed class FocusReturnTests : AccessibleNamesFixture
{
    [TestMethod]
    [DataRow("events-create", "add", "Cancel")]
    [DataRow("events-edit", "edit", "Cancel")]
    [DataRow("events-details", "details", "Close")]
    [DataRow("games-create", "add", "Cancel")]
    [DataRow("games-edit", "edit", "Cancel")]
    [DataRow("activities-create", "add", "Cancel")]
    [DataRow("activities-edit", "edit", "Cancel")]
    [DataRow("activities-details", "details", "Close")]
    [DataRow("themes-create", "add", "Cancel")]
    [DataRow("themes-edit", "edit", "Cancel")]
    [DataRow("holidays-create", "add", "Cancel")]
    [DataRow("holidays-edit", "edit", "Cancel")]
    public async Task ClosingADialog_ReturnsFocusToItsOpener(string form, string keyKind, string closeText)
    {
        var page = await OpenDialog(form);

        var key = Remembered();
        var opener = page.FindAll($"[data-focus-key='{key}']").Single();
        Assert.IsTrue(key == "add" || key.StartsWith(keyKind + "-"), key);
        Assert.IsTrue(key == "add" ? opener.TextContent.Trim().Length > 0 : opener.GetAttribute("title") is not null);
        harness.JSInterop.VerifyNotInvoke("emFocus.restore");

        await page.FormDialog().Button(closeText).ClickAsync(new());

        Assert.IsFalse(page.HasFormDialog());
        harness.JSInterop.VerifyInvoke("emFocus.restore", 1);
        harness.JSInterop.VerifyInvoke("emFocus.remember", 1);
    }

    [TestMethod]
    [DataRow("themes-edit")]
    [DataRow("games-create")]
    [DataRow("events-edit")]
    [DataRow("activities-details")]
    public async Task DismissWithoutChanges_ReturnsFocus(string form)
    {
        var page = await OpenDialog(form);
        var dialog = page.FormDialog();

        await dialog.TriggerEventAsync("ondialogdismiss", new DialogEventArgs { Id = dialog.Id, Reason = "dismiss" });

        Assert.IsFalse(page.HasFormDialog());
        harness.JSInterop.VerifyInvoke("emFocus.restore", 1);
    }

    [TestMethod]
    [DataRow("themes")]
    [DataRow("events")]
    public async Task Discard_ReturnsFocusToTheEditButton_NotTheQuestion(string route)
    {
        var page = await OpenDialog(route + "-edit");
        var key = Remembered();
        var field = route == "events"
            ? page.FormDialog().QuerySelector("fluent-text-field[placeholder='Enter event name...']")!
            : page.FormDialog().QuerySelector("fluent-text-field")!;
        await field.ChangeAsync(new ChangeEventArgs { Value = "Typed but not saved" });
        var dialog = page.FormDialog();

        await dialog.TriggerEventAsync("ondialogdismiss", new DialogEventArgs { Id = dialog.Id, Reason = "dismiss" });
        Assert.IsTrue(page.HasConfirmation());
        // Keep editing: focus stays in the form dialog, nothing is restored yet.
        await page.Confirmation().Button("Keep editing").ClickAsync(new());
        harness.JSInterop.VerifyNotInvoke("emFocus.restore");

        await page.FormDialog().TriggerEventAsync("ondialogdismiss", new DialogEventArgs { Id = page.FormDialog().Id, Reason = "dismiss" });
        await page.ConfirmAsync("Discard");

        Assert.IsFalse(page.HasFormDialog());
        // The question over the dialog never replaced the remembered Edit button.
        var remembered = harness.JSInterop.VerifyInvoke("emFocus.remember", 1);
        Assert.AreEqual(key, remembered[0].Arguments[0]);
        harness.JSInterop.VerifyInvoke("emFocus.restore", 1);
    }

    [TestMethod]
    [DataRow("events", "Game night")]
    [DataRow("games", "Beta")]
    [DataRow("activities", "Plain")]
    [DataRow("themes", "Spooky")]
    [DataRow("holidays", "Halloween")]
    public async Task DeleteConfirmation_CancelReturnsFocusToDeleteButton(string route, string item)
    {
        var page = Open(route);
        var delete = Row(page, item).QuerySelector("fluent-button[title=Delete]")!;
        var key = delete.GetAttribute("data-focus-key");

        await delete.ClickAsync(new());

        Assert.AreEqual(key, Assert.ContainsSingle(harness.JSInterop.VerifyInvoke("emFocus.remember", 1)).Arguments[0]);
        await page.Confirmation().Button("Cancel").ClickAsync(new());
        Assert.IsFalse(page.HasConfirmation());
        harness.JSInterop.VerifyInvoke("emFocus.restore", 1);
    }

    [TestMethod]
    public async Task DeleteConfirmed_RestoresFocusAfterTheDelete()
    {
        var page = Open("themes");
        api.Status(HttpMethod.Delete, $"/api/themes/{spooky.Id}", System.Net.HttpStatusCode.NoContent)
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>());

        await Row(page, "Spooky").QuerySelector("fluent-button[title=Delete]")!.ClickAsync(new());
        await page.ConfirmAsync();

        Assert.AreEqual(1, api.Count(HttpMethod.Delete, $"/api/themes/{spooky.Id}"));
        harness.JSInterop.VerifyInvoke("emFocus.restore", 1);
    }

    [TestMethod]
    public async Task SaveThatClosesTheDialog_RestoresFocus()
    {
        var page = Open("games");
        api.Status(HttpMethod.Put, $"/api/games/{alpha.Id}", System.Net.HttpStatusCode.OK);

        await Row(page, "Alpha").QuerySelector("fluent-button[title=Edit]")!.ClickAsync(new());
        await page.FormDialog().QuerySelector("form")!.SubmitAsync();

        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        Assert.AreEqual(1, api.Count(HttpMethod.Put, $"/api/games/{alpha.Id}"));
        harness.JSInterop.VerifyInvoke("emFocus.restore", 1);
    }

    [TestMethod]
    public async Task FocusReturn_SwallowsJavaScriptFailures()
    {
        var js = new BunitJSInterop { Mode = JSRuntimeMode.Strict };
        js.SetupVoid("emFocus.remember", _ => true).SetException(new JSException("emFocus is not defined"));
        js.SetupVoid("emFocus.restore").SetException(new JSDisconnectedException("gone"));
        var focus = new FocusReturn(js.JSRuntime);

        await focus.RememberAsync("edit-1");
        await focus.RestoreAsync();

        Assert.AreEqual("edit-1", Assert.ContainsSingle(js.Invocations["emFocus.remember"]).Arguments[0]);
        Assert.ContainsSingle(js.Invocations["emFocus.restore"]);
    }

    // The key the page passed to emFocus.remember when its one dialog opened.
    private string Remembered() =>
        (string)Assert.ContainsSingle(harness.JSInterop.VerifyInvoke("emFocus.remember", 1)).Arguments[0]!;
}
