using Bunit;
using Microsoft.AspNetCore.Components;
using static MW_GC.EventManager.Web.Tests.CommentsPanelView;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// #131: unsaved Comment text in the Activity details dialog. A dirty add box or a changed edit asks before the
// dialog is dismissed; an unchanged edit does not, and Close still closes at once.
[TestClass]
public sealed class CommentsPanelDiscardTests : CommentsPanelFixture
{
    [TestMethod]
    public async Task ADirtyAddBox_AsksToDiscard_AndCloseStillClosesAtOnce()
    {
        var page = await OpenDetails();

        // Typed, cursor still in the box: no change event yet, and it already counts.
        await TypeNew(page, "Half a thought");
        await Dismiss(page);

        Assert.AreEqual("Discard changes?", page.Confirmation().QuerySelector("h4")!.TextContent.Trim());
        await page.Confirmation().Button("Keep editing").ClickAsync(new());
        Assert.IsTrue(page.HasFormDialog());
        Assert.AreEqual("Half a thought", NewBox(page)!.GetAttribute("value"));

        await page.FormDialog().Button("Close").ClickAsync(new());
        Assert.IsFalse(page.HasFormDialog());
        Assert.IsFalse(page.HasConfirmation());
        Assert.IsEmpty(api.Requests.Where(r => r.Method != HttpMethod.Get).ToList());
    }

    [TestMethod]
    public async Task ADirtyEdit_AsksToDiscard_AnUnchangedEditDoesNot()
    {
        var page = await OpenDetails();

        await Click(ButtonNamed(page, $"Edit Comment from {AAdded}"), page);
        await Dismiss(page);
        Assert.IsFalse(page.HasConfirmation());
        Assert.IsFalse(page.HasFormDialog());

        await DetailsButton(page).ClickAsync(new());
        page.WaitForAssertion(() => Assert.HasCount(2, Items(page)));
        await Click(ButtonNamed(page, $"Edit Comment from {AAdded}"), page);
        await EditBox(page)!.InputAsync(new ChangeEventArgs { Value = "Changed" });
        await Dismiss(page);

        Assert.IsTrue(page.HasConfirmation());
        await page.ConfirmAsync("Discard");
        Assert.IsFalse(page.HasFormDialog());
        Assert.IsEmpty(api.Requests.Where(r => r.Method != HttpMethod.Get).ToList());
    }
}
