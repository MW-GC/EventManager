using System.Net;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using static MW_GC.EventManager.Web.Tests.CommentsPanelView;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// #131: the busy guard of the Activity details dialog's Comments panel. While an add, save or delete runs, a second
// click sends nothing more and the dialog cannot be closed.
[TestClass]
public sealed class CommentsPanelBusyGuardTests : CommentsPanelFixture
{
    [TestMethod]
    [DataRow("add")]
    [DataRow("save")]
    [DataRow("delete")]
    public async Task ASecondClickWhileARequestRuns_SendsOneRequest_AndTheDialogStaysOpen(string action)
    {
        var page = await OpenDetails();
        TaskCompletionSource gate;
        Task first;
        Func<IElement> button;
        HttpMethod method;
        string path;
        switch (action)
        {
            case "add":
                (method, path) = (HttpMethod.Post, listPath);
                gate = api.Hold(method, path, () => CommentResponse(new Stored(Guid.NewGuid(), "New", DateTimeOffset.UtcNow, TagC), HttpStatusCode.Created));
                await TypeNew(page, "New");
                button = () => page.FormDialog().Button("Add Comment");
                first = button().ClickAsync(new());
                break;
            case "save":
                (method, path) = (HttpMethod.Put, OnePath(a));
                gate = api.Hold(method, path, () => CommentResponse(a with { Text = "Edited", Tag = TagA2 }));
                await Click(ButtonNamed(page, $"Edit Comment from {AAdded}"), page);
                await EditBox(page)!.InputAsync(new ChangeEventArgs { Value = "Edited" });
                button = () => page.FormDialog().Button("Save");
                first = button().ClickAsync(new());
                break;
            default:
                (method, path) = (HttpMethod.Delete, OnePath(a));
                gate = api.Hold(method, path, () => new HttpResponseMessage(HttpStatusCode.NoContent));
                button = () => ButtonNamed(page, $"Delete Comment from {AAdded}");
                await Click(button(), page);
                // Not awaited: the confirmation's answer waits for the held request.
                first = page.ConfirmAsync();
                break;
        }

        // The click may still be queued behind other renderer work, so wait for its render.
        page.WaitForAssertion(() =>
        {
            Assert.IsTrue(button().IsBusy(), "the button of the running request");
            Assert.IsTrue(page.FormDialog().Button("Close").IsBusy(), "the Close button");
            Assert.IsTrue(page.FormDialog().QuerySelector("fluent-button[title=Close]")!.IsBusy(), "the Close icon");
        });
        await button().ClickAsync(new());
        Assert.IsFalse(page.HasConfirmation(), "a second delete question");
        // Overlay click, Escape and Close do nothing while it runs.
        await Dismiss(page);
        await page.FormDialog().Button("Close").ClickAsync(new());
        Assert.IsTrue(page.HasFormDialog());
        Assert.IsFalse(page.HasConfirmation());

        gate.SetResult();
        await first;

        page.WaitForAssertion(() => Assert.IsFalse(page.FormDialog().Button("Close").IsBusy()));
        Assert.AreEqual(1, api.Count(method, path));
        Assert.IsEmpty(page.FormDialog().Alerts());
    }
}
