using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using static MW_GC.EventManager.Web.Tests.CommentsPanelView;

namespace MW_GC.EventManager.Web.Tests;

// #131: stale edits in the Activity details dialog's Comments panel. Each Comment carries its own eTag (ADR 0001),
// so a conflict or a not-found on one Comment shows its message beside that Comment with Reload, and two Comments
// never conflict with each other.
[TestClass]
public sealed class CommentsPanelStaleEditTests : CommentsPanelFixture
{
    [TestMethod]
    public async Task AConflictOnEdit_ShowsTheStaleMessage_KeepsTheText_AndReloadReReadsTheList()
    {
        var page = await OpenDetails();
        api.Status(HttpMethod.Put, OnePath(a), HttpStatusCode.Conflict, "The request conflicts with the stored data. Reload and try again.");

        await Click(ButtonNamed(page, $"Edit Comment from {AAdded}"), page);
        await EditBox(page)!.InputAsync(new ChangeEventArgs { Value = "Mine" });
        await Click(page.FormDialog().Button("Save"), page);

        Assert.AreEqual(StaleMessage, Assert.ContainsSingle(Items(page)[0].Alerts()));
        Assert.IsEmpty(Items(page)[1].Alerts());
        // Not wiped: the typed text stays until the user chooses Reload.
        Assert.AreEqual("Mine", EditBox(page)!.GetAttribute("value"));
        Assert.AreEqual(1, api.Count(HttpMethod.Get, listPath));

        List(a with { Text = "Theirs", Tag = TagA2 }, b);
        await Click(Items(page)[0].Button("Reload"), page);

        Assert.AreEqual(2, api.Count(HttpMethod.Get, listPath));
        CollectionAssert.AreEqual(new[] { "Theirs", b.Text }, Texts(page));
        Assert.IsNull(EditBox(page));
        Assert.IsEmpty(page.FormDialog().Alerts());

        // The next save sends the tag that Reload read.
        api.On(HttpMethod.Put, OnePath(a), () => CommentResponse(a with { Text = "Mine again", Tag = TagC }));
        await Click(ButtonNamed(page, $"Edit Comment from {AAdded}"), page);
        await EditBox(page)!.InputAsync(new ChangeEventArgs { Value = "Mine again" });
        await Click(page.FormDialog().Button("Save"), page);
        CollectionAssert.AreEqual(new[] { TagA, TagA2 }, api.IfMatches(HttpMethod.Put, OnePath(a)));
    }

    [TestMethod]
    public async Task AConflictOnDelete_ShowsTheStaleMessage_KeepsTheComment_AndReloadReReadsTheList()
    {
        var page = await OpenDetails();
        api.Status(HttpMethod.Delete, OnePath(b), HttpStatusCode.Conflict, "The request conflicts with the stored data. Reload and try again.");

        await Click(ButtonNamed(page, $"Delete Comment from {BAdded}"), page);
        await page.ConfirmAsync();
        page.WaitForAssertion(() => Assert.IsFalse(page.FormDialog().Button("Close").IsBusy()));

        Assert.AreEqual(TagB, Assert.ContainsSingle(api.IfMatches(HttpMethod.Delete, OnePath(b))));
        Assert.AreEqual(StaleMessage, Assert.ContainsSingle(Items(page)[1].Alerts()));
        CollectionAssert.AreEqual(new[] { a.Text, b.Text }, Texts(page));

        List(a, b with { Text = "Edited elsewhere", Tag = TagC });
        await Click(Items(page)[1].Button("Reload"), page);

        CollectionAssert.AreEqual(new[] { a.Text, "Edited elsewhere" }, Texts(page));
        Assert.IsEmpty(page.FormDialog().Alerts());
        Assert.AreEqual(2, api.Count(HttpMethod.Get, listPath));
    }

    // The API answers an edit of a Comment deleted meanwhile with 404. (A delete of one is a 204, as for a
    // Comment still there: ActivityDetailsDialogTests.ADeleteAnsweredWith204_RemovesOnlyThatRow_WithoutAMessageOrASecondListRead.)
    [TestMethod]
    public async Task ANotFoundOnEdit_SaysItMayHaveBeenDeleted_WithReload()
    {
        var page = await OpenDetails();
        api.Status(HttpMethod.Put, OnePath(a), HttpStatusCode.NotFound);
        await Click(ButtonNamed(page, $"Edit Comment from {AAdded}"), page);
        await EditBox(page)!.InputAsync(new ChangeEventArgs { Value = "Too late" });
        await Click(page.FormDialog().Button("Save"), page);

        Assert.AreEqual(NotFound, Assert.ContainsSingle(Items(page)[0].Alerts()));
        Assert.AreEqual("Too late", EditBox(page)!.GetAttribute("value"));

        List(b);
        await Click(Items(page)[0].Button("Reload"), page);
        CollectionAssert.AreEqual(new[] { b.Text }, Texts(page));
        Assert.IsEmpty(page.FormDialog().Alerts());
    }

    // Two admins editing different Comments never conflict: each Comment carries its own tag.
    [TestMethod]
    public async Task SavingCommentAThenCommentB_SendsEachCommentsOwnTag()
    {
        var page = await OpenDetails();
        api.On(HttpMethod.Put, OnePath(a), () => CommentResponse(a with { Text = "A edited", Tag = TagA2 }))
           .On(HttpMethod.Put, OnePath(b), () => CommentResponse(b with { Text = "B edited", Tag = TagC }));

        await Click(ButtonNamed(page, $"Edit Comment from {AAdded}"), page);
        await EditBox(page)!.InputAsync(new ChangeEventArgs { Value = "A edited" });
        await Click(page.FormDialog().Button("Save"), page);
        await Click(ButtonNamed(page, $"Edit Comment from {BAdded}"), page);
        await EditBox(page)!.InputAsync(new ChangeEventArgs { Value = "B edited" });
        await Click(page.FormDialog().Button("Save"), page);

        Assert.AreEqual(TagA, Assert.ContainsSingle(api.IfMatches(HttpMethod.Put, OnePath(a))));
        Assert.AreEqual(TagB, Assert.ContainsSingle(api.IfMatches(HttpMethod.Put, OnePath(b))));
        CollectionAssert.AreEqual(new[] { "A edited", "B edited" }, Texts(page));

        // A's next save sends the tag A's own save returned.
        api.On(HttpMethod.Delete, OnePath(a), () => new HttpResponseMessage(HttpStatusCode.NoContent));
        await Click(ButtonNamed(page, $"Delete Comment from {AAdded}"), page);
        await page.ConfirmAsync();
        page.WaitForAssertion(() => Assert.AreEqual(TagA2, Assert.ContainsSingle(api.IfMatches(HttpMethod.Delete, OnePath(a)))));
    }
}
