using System.Globalization;
using System.Net;
using Bunit;
using static MW_GC.EventManager.Web.Tests.CommentsPanelView;

namespace MW_GC.EventManager.Web.Tests;

// #131: the Comments panel's list in the Activity details dialog: every Comment of the Activity in the API's
// order with its added date, the empty and loading states, and a failed load. The panel's other behaviour is in
// the sibling CommentsPanel*Tests files; the shared page and stub API are in CommentsPanelFixture.
[TestClass]
public sealed class CommentsPanelTests : CommentsPanelFixture
{
    [TestMethod]
    public async Task Details_ShowTheActivity_AndTheCommentsPanel_NotTheOldEditor()
    {
        var page = await OpenDetails();

        var dialog = page.FormDialog();
        Assert.AreEqual("Race", dialog.GetAttribute("aria-label"));
        Assert.Contains("Kart", dialog.TextContent);
        Assert.Contains("Three laps", dialog.TextContent);
        // The panel reads the Comments rows; the editor of the old text is gone.
        CollectionAssert.AreEqual(new[] { a.Text, b.Text }, Texts(page));
        Assert.DoesNotContain("Event Manager Comments", dialog.TextContent);
        Assert.IsEmpty(dialog.QuerySelectorAll("fluent-button").Where(x => x.TextContent.Trim() == "Save Comments").ToList());
        Assert.AreEqual("New Comment", Assert.ContainsSingle(dialog.QuerySelectorAll("fluent-text-area").ToList()).GetAttribute("aria-label"));
        Assert.AreEqual(1, api.Count(HttpMethod.Get, listPath));
        Assert.AreEqual(0, api.Requests.Count(r => r.Method == HttpMethod.Patch));
    }

    [TestMethod]
    public async Task TheListShowsDatesAndTextInApiOrder_WithLineBreaksKept()
    {
        // The shown date is the current culture's short date; the invariant culture writes it MM/dd/yyyy.
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        // The API answers oldest first; the panel keeps whatever order it gets and never sorts again.
        var multiLine = a with { Text = "Ran long\nCut the second round" };
        List(b, multiLine);
        var page = await OpenDetails();

        CollectionAssert.AreEqual(new[] { b.Text, "Ran long\nCut the second round" }, Texts(page));
        var times = Items(page).Select(i => i.QuerySelector("time")!).ToList();
        // The datetime attribute is the instant in UTC, whatever the local zone.
        Assert.IsTrue(times.All(t => t.GetAttribute("datetime")!.EndsWith('Z')), "datetime in UTC");
        CollectionAssert.AreEqual(new[] { b.Added, a.Added }, times.Select(t => DateTimeOffset.Parse(t.GetAttribute("datetime")!, CultureInfo.InvariantCulture)).ToList());
        CollectionAssert.AreEqual(new[] { "06/01/2025", "03/14/2025" }, times.Select(t => t.TextContent.Trim()).ToList());
        Assert.Contains("white-space: pre-wrap", Items(page)[1].QuerySelector(".comment-text")!.GetAttribute("style")!);
        Assert.IsNotNull(ButtonNamed(page, $"Edit Comment from {AAdded}"));
        Assert.IsNotNull(ButtonNamed(page, $"Delete Comment from {AAdded}"));
        Assert.IsNotNull(ButtonNamed(page, $"Edit Comment from {BAdded}"));
        Assert.IsNotNull(ButtonNamed(page, $"Delete Comment from {BAdded}"));
        Assert.DoesNotContain("No Comments yet.", page.FormDialog().TextContent);
    }

    [TestMethod]
    public async Task NoComments_ShowsNoCommentsYet()
    {
        List();
        var page = await OpenDetails();

        Assert.Contains("No Comments yet.", page.FormDialog().TextContent);
        Assert.IsEmpty(Items(page));
        Assert.IsNotNull(NewBox(page));
    }

    [TestMethod]
    public async Task Loading_ShowsAProgressRingLabelledLoadingComments()
    {
        var gate = api.Hold(HttpMethod.Get, listPath, () => ListResponse(a));
        var page = Open();
        await DetailsButton(page).ClickAsync(new());

        var ring = Assert.ContainsSingle(page.FormDialog().QuerySelectorAll("fluent-progress-ring").ToList());
        Assert.AreEqual("Loading Comments...", ring.GetAttribute("aria-label"));
        Assert.IsNull(NewBox(page));

        gate.SetResult();
        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { a.Text }, Texts(page)));
        Assert.IsEmpty(page.FormDialog().QuerySelectorAll("fluent-progress-ring"));
    }

    [TestMethod]
    public async Task AFailedLoadShowsItsMessageInThePanel_AndRetryLoadsTheList()
    {
        api.Status(HttpMethod.Get, listPath, HttpStatusCode.InternalServerError);
        var page = Open();
        await DetailsButton(page).ClickAsync(new());

        page.WaitForAssertion(() => Assert.Contains($"Comments could not be loaded. {ServerError}", page.FormDialog().TextContent));
        Assert.IsNull(NewBox(page));
        Assert.IsEmpty(page.PageAlerts());

        List(a);
        await page.FormDialog().Button("Retry").ClickAsync(new());

        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { a.Text }, Texts(page)));
        Assert.DoesNotContain("could not be loaded", page.FormDialog().TextContent);
        Assert.AreEqual(2, api.Count(HttpMethod.Get, listPath));
    }
}
