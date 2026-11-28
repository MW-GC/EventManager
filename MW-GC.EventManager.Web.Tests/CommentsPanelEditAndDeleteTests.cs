using System.Globalization;
using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using static MW_GC.EventManager.Web.Tests.CommentsPanelView;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// #131: editing and deleting one Comment in the Activity details dialog's Comments panel. An edit or delete sends
// that Comment's own eTag as If-Match (ADR 0001), one Comment is edited at a time, and a failed save keeps the text.
[TestClass]
public sealed class CommentsPanelEditAndDeleteTests : CommentsPanelFixture
{
    [TestMethod]
    public async Task Edit_SaveSendsTheTextOnlyWithThatCommentsIfMatch_AndKeepsTheDate()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var page = await OpenDetails();
        var path = OnePath(a);
        api.On(HttpMethod.Put, path, () => CommentResponse(a with { Text = "Ran longer", Tag = TagA2 }));

        await Click(ButtonNamed(page, $"Edit Comment from {AAdded}"), page);
        Assert.AreEqual(a.Text, EditBox(page)!.GetAttribute("value"));
        Assert.AreEqual($"Edit Comment from {AAdded}", EditBox(page)!.GetAttribute("aria-label"));
        await EditBox(page)!.InputAsync(new ChangeEventArgs { Value = " Ran longer " });
        await Click(page.FormDialog().Button("Save"), page);

        Assert.AreEqual("{\"text\":\"Ran longer\"}", Assert.ContainsSingle(api.Bodies(HttpMethod.Put, path)));
        Assert.AreEqual(TagA, Assert.ContainsSingle(api.IfMatches(HttpMethod.Put, path)));
        CollectionAssert.AreEqual(new[] { "Ran longer", b.Text }, Texts(page));
        Assert.AreEqual("03/14/2025", Items(page)[0].QuerySelector("time")!.TextContent.Trim());
        Assert.IsNull(EditBox(page));
        Assert.IsEmpty(page.FormDialog().Alerts());
        Assert.AreEqual(0, api.Count(HttpMethod.Put, OnePath(b)));
    }

    [TestMethod]
    [DataRow(" \n ", Required)]
    [DataRow("1001", TooLong)]
    public async Task Edit_BlankOrTooLong_IsRefused_AndSendsNothing(string typed, string message)
    {
        if (typed == "1001") typed = new string('x', 1001);
        var page = await OpenDetails();

        await Click(ButtonNamed(page, $"Edit Comment from {AAdded}"), page);
        await EditBox(page)!.InputAsync(new ChangeEventArgs { Value = typed });
        await Click(page.FormDialog().Button("Save"), page);

        Assert.AreEqual(message, Assert.ContainsSingle(EditBox(page)!.ParentElement!.Alerts()));
        Assert.AreEqual(0, api.Requests.Count(r => r.Method == HttpMethod.Put));
        Assert.AreEqual(typed, EditBox(page)!.GetAttribute("value"));
    }

    [TestMethod]
    public async Task Edit_CancelDropsTheEdit_AndSendsNothing()
    {
        var page = await OpenDetails();

        await Click(ButtonNamed(page, $"Edit Comment from {AAdded}"), page);
        await EditBox(page)!.InputAsync(new ChangeEventArgs { Value = "Typed but not saved" });
        await Click(page.FormDialog().Button("Cancel"), page);

        Assert.IsNull(EditBox(page));
        CollectionAssert.AreEqual(new[] { a.Text, b.Text }, Texts(page));
        Assert.IsEmpty(api.Requests.Where(r => r.Method != HttpMethod.Get).ToList());
        // Nothing left to discard: Escape closes at once.
        await Dismiss(page);
        Assert.IsFalse(page.HasConfirmation());
        Assert.IsFalse(page.HasFormDialog());
    }

    [TestMethod]
    public async Task Edit_OneCommentAtATime()
    {
        var page = await OpenDetails();
        Assert.IsFalse(ButtonNamed(page, $"Edit Comment from {BAdded}").IsBusy());

        await Click(ButtonNamed(page, $"Edit Comment from {AAdded}"), page);

        Assert.ContainsSingle(page.FormDialog().QuerySelectorAll("fluent-text-area[aria-label^='Edit Comment']").ToList());
        Assert.IsTrue(ButtonNamed(page, $"Edit Comment from {BAdded}").IsBusy());
        await Click(page.FormDialog().Button("Cancel"), page);
        Assert.IsFalse(ButtonNamed(page, $"Edit Comment from {BAdded}").IsBusy());
    }

    [TestMethod]
    public async Task AFailedEditSaveKeepsTheTextAndSaysWhy_AndSaveTriesAgain()
    {
        var page = await OpenDetails();
        var path = OnePath(a);
        api.Status(HttpMethod.Put, path, HttpStatusCode.InternalServerError);

        await Click(ButtonNamed(page, $"Edit Comment from {AAdded}"), page);
        await EditBox(page)!.InputAsync(new ChangeEventArgs { Value = "Ran longer" });
        await Click(page.FormDialog().Button("Save"), page);

        Assert.AreEqual($"The Comment was not saved. {ServerError}", Assert.ContainsSingle(Items(page)[0].Alerts()));
        Assert.AreEqual("Ran longer", EditBox(page)!.GetAttribute("value"));
        Assert.IsEmpty(Items(page)[0].QuerySelectorAll("fluent-button").Where(x => x.TextContent.Trim() == "Reload").ToList());

        api.On(HttpMethod.Put, path, () => CommentResponse(a with { Text = "Ran longer", Tag = TagA2 }));
        await Click(page.FormDialog().Button("Save"), page);
        CollectionAssert.AreEqual(new[] { "Ran longer", b.Text }, Texts(page));
        Assert.IsEmpty(page.FormDialog().Alerts());
        CollectionAssert.AreEqual(new[] { TagA, TagA }, api.IfMatches(HttpMethod.Put, path));
    }

    [TestMethod]
    public async Task Delete_AsksNamingTheComment_CancelSendsNothing_ConfirmSendsIfMatchAndRemovesTheRow()
    {
        var page = await OpenDetails();
        var path = OnePath(a);
        api.Status(HttpMethod.Delete, path, HttpStatusCode.NoContent);

        await Click(ButtonNamed(page, $"Delete Comment from {AAdded}"), page);
        Assert.AreEqual($"Delete Comment from {AAdded}?", page.Confirmation().QuerySelector(".confirm-message")!.TextContent.Trim());
        await page.Confirmation().Button("Cancel").ClickAsync(new());

        Assert.IsFalse(page.HasConfirmation());
        Assert.IsTrue(page.HasFormDialog());
        Assert.AreEqual(0, api.Count(HttpMethod.Delete, path));
        Assert.HasCount(2, Items(page));

        await Click(ButtonNamed(page, $"Delete Comment from {AAdded}"), page);
        await page.ConfirmAsync();
        page.WaitForAssertion(() => Assert.IsFalse(page.FormDialog().Button("Close").IsBusy()));

        Assert.AreEqual(TagA, Assert.ContainsSingle(api.IfMatches(HttpMethod.Delete, path)));
        CollectionAssert.AreEqual(new[] { b.Text }, Texts(page));
        Assert.IsTrue(page.HasFormDialog());
        Assert.AreEqual(0, api.Count(HttpMethod.Delete, OnePath(b)));
    }
}
