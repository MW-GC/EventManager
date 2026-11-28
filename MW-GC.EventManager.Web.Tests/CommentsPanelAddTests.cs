using System.Globalization;
using System.Net;
using MW_GC.EventManager.API.Validation;
using static MW_GC.EventManager.Web.Tests.CommentsPanelView;

namespace MW_GC.EventManager.Web.Tests;

// #131: adding a Comment in the Activity details dialog's Comments panel: the returned Comment is appended,
// blank and too-long text is refused before any request, and a failed add keeps the text and says why.
[TestClass]
public sealed class CommentsPanelAddTests : CommentsPanelFixture
{
    [TestMethod]
    public async Task Add_AppendsTheReturnedComment_TrimsTheText_AndEmptiesTheBox()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        List(a);
        var page = await OpenDetails();
        var added = new Stored(Guid.NewGuid(), "Cut the second round", AddedAtLocal(2026, 11, 20, 12, 0), TagC);
        api.On(HttpMethod.Post, listPath, () => CommentResponse(added, HttpStatusCode.Created));

        await TypeNew(page, "  Cut the second round \n ");
        await Click(page.FormDialog().Button("Add Comment"), page);

        Assert.AreEqual("{\"text\":\"Cut the second round\"}", Assert.ContainsSingle(api.Bodies(HttpMethod.Post, listPath)));
        Assert.IsNull(Assert.ContainsSingle(api.IfMatches(HttpMethod.Post, listPath)));
        CollectionAssert.AreEqual(new[] { a.Text, added.Text }, Texts(page));
        Assert.AreEqual("11/20/2026", Items(page)[1].QuerySelector("time")!.TextContent.Trim());
        Assert.AreEqual("", NewBox(page)!.GetAttribute("value") ?? "");
        Assert.IsEmpty(page.FormDialog().Alerts());
        // Appended from the answer, not read again.
        Assert.AreEqual(1, api.Count(HttpMethod.Get, listPath));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   \n  ")]
    public async Task Add_Blank_IsRefused_AndSendsNothing(string typed)
    {
        var page = await OpenDetails();

        await TypeNew(page, typed);
        await Click(page.FormDialog().Button("Add Comment"), page);

        Assert.AreEqual(Required, Assert.ContainsSingle(NewBox(page)!.ParentElement!.Alerts()));
        Assert.AreEqual(0, api.Count(HttpMethod.Post, listPath));
        CollectionAssert.AreEqual(new[] { a.Text, b.Text }, Texts(page));
    }

    [TestMethod]
    public async Task Add_1001Characters_IsRefused_AndSendsNothing_AndKeepsTheText()
    {
        var page = await OpenDetails();
        var typed = new string('x', 1001);

        await TypeNew(page, typed);
        await Click(page.FormDialog().Button("Add Comment"), page);

        Assert.AreEqual(TooLong, Assert.ContainsSingle(NewBox(page)!.ParentElement!.Alerts()));
        Assert.AreEqual(0, api.Count(HttpMethod.Post, listPath));
        Assert.AreEqual(typed, NewBox(page)!.GetAttribute("value"));
    }

    [TestMethod]
    public async Task Add_Exactly1000Characters_IsAccepted_EvenWithSpacesAround()
    {
        var page = await OpenDetails();
        var text = new string('x', 1000);
        api.On(HttpMethod.Post, listPath, () => CommentResponse(new Stored(Guid.NewGuid(), text, DateTimeOffset.UtcNow, TagC), HttpStatusCode.Created));

        await TypeNew(page, "  " + text + "  ");
        await Click(page.FormDialog().Button("Add Comment"), page);

        Assert.AreEqual($"{{\"text\":\"{text}\"}}", Assert.ContainsSingle(api.Bodies(HttpMethod.Post, listPath)));
        Assert.IsEmpty(page.FormDialog().Alerts());
        Assert.HasCount(3, Items(page));
    }

    // The panel's messages and limit are the API's own (CommentValidator), copied, not shared: the Web's words
    // and the API's are each checked against the same literals, so a change on either side fails here.
    [TestMethod]
    public async Task TheMessagesAndTheLimitAreTheApis()
    {
        Assert.AreEqual("Comment text is required.", CommentValidator.ValidateText("  ", out _));
        Assert.AreEqual("Comment text must be 1000 characters or fewer.", CommentValidator.ValidateText(new string('x', 1001), out _));
        Assert.IsNull(CommentValidator.ValidateText(new string('x', 1000), out _));
        var page = await OpenDetails();

        await TypeNew(page, "  ");
        await Click(page.FormDialog().Button("Add Comment"), page);
        Assert.AreEqual("Comment text is required.", Assert.ContainsSingle(page.FormDialog().Alerts()));

        await TypeNew(page, new string('x', 1001));
        await Click(page.FormDialog().Button("Add Comment"), page);
        Assert.AreEqual("Comment text must be 1000 characters or fewer.", Assert.ContainsSingle(page.FormDialog().Alerts()));
        Assert.AreEqual(0, api.Count(HttpMethod.Post, listPath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AFailedAddKeepsTheTextAndSaysWhy(bool offline)
    {
        var page = await OpenDetails();
        if (offline) api.Offline(HttpMethod.Post, listPath);
        else api.Status(HttpMethod.Post, listPath, HttpStatusCode.InternalServerError);

        await TypeNew(page, "Ran long again");
        await Click(page.FormDialog().Button("Add Comment"), page);

        Assert.AreEqual($"The Comment was not added. {(offline ? Unreachable : ServerError)}",
            Assert.ContainsSingle(NewBox(page)!.ParentElement!.Alerts()));
        Assert.AreEqual("Ran long again", NewBox(page)!.GetAttribute("value"));
        Assert.IsFalse(page.FormDialog().Button("Add Comment").IsBusy());
        CollectionAssert.AreEqual(new[] { a.Text, b.Text }, Texts(page));
        Assert.IsEmpty(page.PageAlerts());
    }
}
