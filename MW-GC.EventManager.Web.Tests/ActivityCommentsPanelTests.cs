using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;

namespace MW_GC.EventManager.Web.Tests;

// #131: the Comments panel in the Activity details dialog, rendered with bUnit through the Activities page
// against a stub API. Every Comment of the Activity shows in the API's order with its added date, and an
// admin adds, edits and deletes them one at a time. Each Comment is its own row with its own eTag
// (ADR 0001): an edit or delete sends that Comment's tag as If-Match, so two Comments never conflict, and a
// stale tag on one Comment shows the stale-edit message beside it with Reload.
[TestClass]
public sealed class ActivityCommentsPanelTests : IDisposable
{
    // Azure Table Storage tags are weak and carry quotes; they must reach If-Match as read.
    private const string TagA = "W/\"datetime'2026-11-20T10%3A00%3A00.0000000Z'\"";
    private const string TagA2 = "W/\"datetime'2026-11-20T10%3A30%3A00.0000000Z'\"";
    private const string TagB = "W/\"datetime'2026-11-20T11%3A00%3A00.0000000Z'\"";
    private const string TagC = "W/\"datetime'2026-11-20T12%3A00%3A00.0000000Z'\"";
    private const string StaleMessage = "This Comment was changed by someone else. Reload and try again.";
    private const string NotFound = "Not found. It may have been deleted.";
    private const string Required = "Comment text is required.";
    private const string TooLong = "Comment text must be 1000 characters or fewer.";
    private const string ServerError = "The server had a problem (HTTP 500). Try again.";
    private const string Unreachable = "Could not reach the server. Check the connection and try again.";

    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly GameEntity kart = new() { Name = "Kart" };
    private readonly ActivityEntity race;
    private readonly string listPath;
    // Noon UTC, so the local date is the same day in every time zone the tests may run in.
    private readonly Stored a = new(Guid.NewGuid(), "Ran long", new DateTimeOffset(2025, 3, 14, 12, 0, 0, TimeSpan.Zero), TagA);
    private readonly Stored b = new(Guid.NewGuid(), "Crowd favourite", new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero), TagB);

    private sealed record Stored(Guid Id, string Text, DateTimeOffset Added, string Tag)
    {
        // The local short date the panel shows (the card's "CreatedUtc as a local short date").
        public string Date => Added.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
    }

    public ActivityCommentsPanelTests()
    {
        // The Activity still carries the old single note (#133 removes it); the panel never shows it.
        race = new ActivityEntity { Name = "Race", GameId = kart.Id, Description = "Three laps", Comments = "Old note" };
        listPath = $"/api/activities/{race.Id}/comments";
        api.Json(HttpMethod.Get, "/api/games", new[] { kart })
           .Json(HttpMethod.Get, "/api/activities", new[] { race })
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>())
           // The details dialog's Performance section (#137) reads these too; answered so it loads quietly.
           .Json(HttpMethod.Get, "/api/events", Array.Empty<EventEntity>())
           .Json(HttpMethod.Get, "/api/event-metrics", Array.Empty<object>());
        List(a, b);
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    // ---- The list ------------------------------------------------------------------------------

    [TestMethod]
    public async Task Details_ShowTheActivity_AndTheCommentsPanel_NotTheOldEditor()
    {
        var page = await OpenDetails();

        var dialog = page.FormDialog();
        Assert.AreEqual("Race", dialog.GetAttribute("aria-label"));
        Assert.Contains("Kart", dialog.TextContent);
        Assert.Contains("Three laps", dialog.TextContent);
        // The panel reads the Comments rows; the old note on the Activity row is neither shown nor editable.
        CollectionAssert.AreEqual(new[] { a.Text, b.Text }, Texts(page));
        Assert.DoesNotContain("Old note", dialog.TextContent);
        Assert.DoesNotContain("Event Manager Comments", dialog.TextContent);
        Assert.IsEmpty(dialog.QuerySelectorAll("fluent-button").Where(x => x.TextContent.Trim() == "Save Comments").ToList());
        Assert.AreEqual("New Comment", Assert.ContainsSingle(dialog.QuerySelectorAll("fluent-text-area").ToList()).GetAttribute("aria-label"));
        Assert.AreEqual(1, api.Count(HttpMethod.Get, listPath));
        Assert.AreEqual(0, api.Requests.Count(r => r.Method == HttpMethod.Patch));
    }

    [TestMethod]
    public async Task TheListShowsDatesAndTextInApiOrder_WithLineBreaksKept()
    {
        // The API answers oldest first; the panel keeps whatever order it gets and never sorts again.
        var multiLine = a with { Text = "Ran long\nCut the second round" };
        List(b, multiLine);
        var page = await OpenDetails();

        CollectionAssert.AreEqual(new[] { b.Text, "Ran long\nCut the second round" }, Texts(page));
        var times = Items(page).Select(i => i.QuerySelector("time")!).ToList();
        CollectionAssert.AreEqual(new[] { "2025-06-01T12:00:00Z", "2025-03-14T12:00:00Z" }, times.Select(t => t.GetAttribute("datetime")).ToList());
        CollectionAssert.AreEqual(new[] { b.Date, a.Date }, times.Select(t => t.TextContent.Trim()).ToList());
        Assert.AreNotEqual(a.Date, b.Date);
        Assert.Contains("white-space: pre-wrap", Items(page)[1].QuerySelector(".comment-text")!.GetAttribute("style")!);
        Assert.IsNotNull(Btn(page, $"Edit Comment from {a.Date}"));
        Assert.IsNotNull(Btn(page, $"Delete Comment from {a.Date}"));
        Assert.IsNotNull(Btn(page, $"Edit Comment from {b.Date}"));
        Assert.IsNotNull(Btn(page, $"Delete Comment from {b.Date}"));
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

    // ---- Add -----------------------------------------------------------------------------------

    [TestMethod]
    public async Task Add_AppendsTheReturnedComment_TrimsTheText_AndEmptiesTheBox()
    {
        List(a);
        var page = await OpenDetails();
        var added = new Stored(Guid.NewGuid(), "Cut the second round", new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero), TagC);
        api.On(HttpMethod.Post, listPath, () => One(added, HttpStatusCode.Created));

        await TypeNew(page, "  Cut the second round \n ");
        await Click(page.FormDialog().Button("Add Comment"), page);

        Assert.AreEqual("{\"text\":\"Cut the second round\"}", Assert.ContainsSingle(api.Bodies(HttpMethod.Post, listPath)));
        Assert.IsNull(Assert.ContainsSingle(api.IfMatches(HttpMethod.Post, listPath)));
        CollectionAssert.AreEqual(new[] { a.Text, added.Text }, Texts(page));
        Assert.AreEqual(added.Date, Items(page)[1].QuerySelector("time")!.TextContent.Trim());
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
        api.On(HttpMethod.Post, listPath, () => One(new Stored(Guid.NewGuid(), text, DateTimeOffset.UtcNow, TagC), HttpStatusCode.Created));

        await TypeNew(page, "  " + text + "  ");
        await Click(page.FormDialog().Button("Add Comment"), page);

        Assert.AreEqual($"{{\"text\":\"{text}\"}}", Assert.ContainsSingle(api.Bodies(HttpMethod.Post, listPath)));
        Assert.IsEmpty(page.FormDialog().Alerts());
        Assert.HasCount(3, Items(page));
    }

    // The panel's messages and limit are the API's own (CommentValidator), copied, not shared.
    [TestMethod]
    public async Task TheMessagesAndTheLimitAreTheApis()
    {
        var page = await OpenDetails();

        await TypeNew(page, "  ");
        await Click(page.FormDialog().Button("Add Comment"), page);
        Assert.AreEqual(CommentValidator.ValidateText("  ", out _), Assert.ContainsSingle(page.FormDialog().Alerts()));

        var over = new string('x', FieldRules.CommentTextMaxLength + 1);
        await TypeNew(page, over);
        await Click(page.FormDialog().Button("Add Comment"), page);
        Assert.AreEqual(CommentValidator.ValidateText(over, out _), Assert.ContainsSingle(page.FormDialog().Alerts()));
        Assert.IsNull(CommentValidator.ValidateText(new string('x', FieldRules.CommentTextMaxLength), out _));
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

    // ---- Edit ----------------------------------------------------------------------------------

    [TestMethod]
    public async Task Edit_SaveSendsTheTextOnlyWithThatCommentsIfMatch_AndKeepsTheDate()
    {
        var page = await OpenDetails();
        var path = OnePath(a);
        api.On(HttpMethod.Put, path, () => One(a with { Text = "Ran longer", Tag = TagA2 }));

        await Click(Btn(page, $"Edit Comment from {a.Date}"), page);
        Assert.AreEqual(a.Text, EditBox(page)!.GetAttribute("value"));
        Assert.AreEqual($"Edit Comment from {a.Date}", EditBox(page)!.GetAttribute("aria-label"));
        await EditBox(page)!.InputAsync(new ChangeEventArgs { Value = " Ran longer " });
        await Click(page.FormDialog().Button("Save"), page);

        Assert.AreEqual("{\"text\":\"Ran longer\"}", Assert.ContainsSingle(api.Bodies(HttpMethod.Put, path)));
        Assert.AreEqual(TagA, Assert.ContainsSingle(api.IfMatches(HttpMethod.Put, path)));
        CollectionAssert.AreEqual(new[] { "Ran longer", b.Text }, Texts(page));
        Assert.AreEqual(a.Date, Items(page)[0].QuerySelector("time")!.TextContent.Trim());
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

        await Click(Btn(page, $"Edit Comment from {a.Date}"), page);
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

        await Click(Btn(page, $"Edit Comment from {a.Date}"), page);
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
        Assert.IsFalse(Btn(page, $"Edit Comment from {b.Date}").IsBusy());

        await Click(Btn(page, $"Edit Comment from {a.Date}"), page);

        Assert.ContainsSingle(page.FormDialog().QuerySelectorAll("fluent-text-area[aria-label^='Edit Comment']").ToList());
        Assert.IsTrue(Btn(page, $"Edit Comment from {b.Date}").IsBusy());
        await Click(page.FormDialog().Button("Cancel"), page);
        Assert.IsFalse(Btn(page, $"Edit Comment from {b.Date}").IsBusy());
    }

    [TestMethod]
    public async Task AFailedEditSaveKeepsTheTextAndSaysWhy_AndSaveTriesAgain()
    {
        var page = await OpenDetails();
        var path = OnePath(a);
        api.Status(HttpMethod.Put, path, HttpStatusCode.InternalServerError);

        await Click(Btn(page, $"Edit Comment from {a.Date}"), page);
        await EditBox(page)!.InputAsync(new ChangeEventArgs { Value = "Ran longer" });
        await Click(page.FormDialog().Button("Save"), page);

        Assert.AreEqual($"The Comment was not saved. {ServerError}", Assert.ContainsSingle(Items(page)[0].Alerts()));
        Assert.AreEqual("Ran longer", EditBox(page)!.GetAttribute("value"));
        Assert.IsEmpty(Items(page)[0].QuerySelectorAll("fluent-button").Where(x => x.TextContent.Trim() == "Reload").ToList());

        api.On(HttpMethod.Put, path, () => One(a with { Text = "Ran longer", Tag = TagA2 }));
        await Click(page.FormDialog().Button("Save"), page);
        CollectionAssert.AreEqual(new[] { "Ran longer", b.Text }, Texts(page));
        Assert.IsEmpty(page.FormDialog().Alerts());
        CollectionAssert.AreEqual(new[] { TagA, TagA }, api.IfMatches(HttpMethod.Put, path));
    }

    // ---- Delete --------------------------------------------------------------------------------

    [TestMethod]
    public async Task Delete_AsksNamingTheComment_CancelSendsNothing_ConfirmSendsIfMatchAndRemovesTheRow()
    {
        var page = await OpenDetails();
        var path = OnePath(a);
        api.Status(HttpMethod.Delete, path, HttpStatusCode.NoContent);

        await Click(Btn(page, $"Delete Comment from {a.Date}"), page);
        Assert.AreEqual($"Delete Comment from {a.Date}?", page.Confirmation().QuerySelector(".confirm-message")!.TextContent.Trim());
        await page.Confirmation().Button("Cancel").ClickAsync(new());

        Assert.IsFalse(page.HasConfirmation());
        Assert.IsTrue(page.HasFormDialog());
        Assert.AreEqual(0, api.Count(HttpMethod.Delete, path));
        Assert.HasCount(2, Items(page));

        await Click(Btn(page, $"Delete Comment from {a.Date}"), page);
        await page.ConfirmAsync();
        page.WaitForAssertion(() => Assert.IsFalse(page.FormDialog().Button("Close").IsBusy()));

        Assert.AreEqual(TagA, Assert.ContainsSingle(api.IfMatches(HttpMethod.Delete, path)));
        CollectionAssert.AreEqual(new[] { b.Text }, Texts(page));
        Assert.IsTrue(page.HasFormDialog());
        Assert.AreEqual(0, api.Count(HttpMethod.Delete, OnePath(b)));
    }

    // ---- Stale edits ---------------------------------------------------------------------------

    [TestMethod]
    public async Task AConflictOnEdit_ShowsTheStaleMessage_KeepsTheText_AndReloadReReadsTheList()
    {
        var page = await OpenDetails();
        api.Status(HttpMethod.Put, OnePath(a), HttpStatusCode.Conflict, "The request conflicts with the stored data. Reload and try again.");

        await Click(Btn(page, $"Edit Comment from {a.Date}"), page);
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
        api.On(HttpMethod.Put, OnePath(a), () => One(a with { Text = "Mine again", Tag = TagC }));
        await Click(Btn(page, $"Edit Comment from {a.Date}"), page);
        await EditBox(page)!.InputAsync(new ChangeEventArgs { Value = "Mine again" });
        await Click(page.FormDialog().Button("Save"), page);
        CollectionAssert.AreEqual(new[] { TagA, TagA2 }, api.IfMatches(HttpMethod.Put, OnePath(a)));
    }

    [TestMethod]
    public async Task AConflictOnDelete_ShowsTheStaleMessage_KeepsTheComment_AndReloadReReadsTheList()
    {
        var page = await OpenDetails();
        api.Status(HttpMethod.Delete, OnePath(b), HttpStatusCode.Conflict, "The request conflicts with the stored data. Reload and try again.");

        await Click(Btn(page, $"Delete Comment from {b.Date}"), page);
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

    [TestMethod]
    [DataRow("edit")]
    [DataRow("delete")]
    public async Task ANotFoundOnEditOrDelete_SaysItMayHaveBeenDeleted_WithReload(string action)
    {
        var page = await OpenDetails();
        if (action == "edit")
        {
            api.Status(HttpMethod.Put, OnePath(a), HttpStatusCode.NotFound);
            await Click(Btn(page, $"Edit Comment from {a.Date}"), page);
            await EditBox(page)!.InputAsync(new ChangeEventArgs { Value = "Too late" });
            await Click(page.FormDialog().Button("Save"), page);
        }
        else
        {
            api.Status(HttpMethod.Delete, OnePath(a), HttpStatusCode.NotFound);
            await Click(Btn(page, $"Delete Comment from {a.Date}"), page);
            await page.ConfirmAsync();
            page.WaitForAssertion(() => Assert.IsFalse(page.FormDialog().Button("Close").IsBusy()));
        }

        Assert.AreEqual(NotFound, Assert.ContainsSingle(Items(page)[0].Alerts()));

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
        api.On(HttpMethod.Put, OnePath(a), () => One(a with { Text = "A edited", Tag = TagA2 }))
           .On(HttpMethod.Put, OnePath(b), () => One(b with { Text = "B edited", Tag = TagC }));

        await Click(Btn(page, $"Edit Comment from {a.Date}"), page);
        await EditBox(page)!.InputAsync(new ChangeEventArgs { Value = "A edited" });
        await Click(page.FormDialog().Button("Save"), page);
        await Click(Btn(page, $"Edit Comment from {b.Date}"), page);
        await EditBox(page)!.InputAsync(new ChangeEventArgs { Value = "B edited" });
        await Click(page.FormDialog().Button("Save"), page);

        Assert.AreEqual(TagA, Assert.ContainsSingle(api.IfMatches(HttpMethod.Put, OnePath(a))));
        Assert.AreEqual(TagB, Assert.ContainsSingle(api.IfMatches(HttpMethod.Put, OnePath(b))));
        CollectionAssert.AreEqual(new[] { "A edited", "B edited" }, Texts(page));

        // A's next save sends the tag A's own save returned.
        api.On(HttpMethod.Delete, OnePath(a), () => new HttpResponseMessage(HttpStatusCode.NoContent));
        await Click(Btn(page, $"Delete Comment from {a.Date}"), page);
        await page.ConfirmAsync();
        page.WaitForAssertion(() => Assert.AreEqual(TagA2, Assert.ContainsSingle(api.IfMatches(HttpMethod.Delete, OnePath(a)))));
    }

    // ---- Busy guard ----------------------------------------------------------------------------

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
                gate = api.Hold(method, path, () => One(new Stored(Guid.NewGuid(), "New", DateTimeOffset.UtcNow, TagC), HttpStatusCode.Created));
                await TypeNew(page, "New");
                button = () => page.FormDialog().Button("Add Comment");
                first = button().ClickAsync(new());
                break;
            case "save":
                (method, path) = (HttpMethod.Put, OnePath(a));
                gate = api.Hold(method, path, () => One(a with { Text = "Edited", Tag = TagA2 }));
                await Click(Btn(page, $"Edit Comment from {a.Date}"), page);
                await EditBox(page)!.InputAsync(new ChangeEventArgs { Value = "Edited" });
                button = () => page.FormDialog().Button("Save");
                first = button().ClickAsync(new());
                break;
            default:
                (method, path) = (HttpMethod.Delete, OnePath(a));
                gate = api.Hold(method, path, () => new HttpResponseMessage(HttpStatusCode.NoContent));
                button = () => Btn(page, $"Delete Comment from {a.Date}");
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

    // ---- Discard -------------------------------------------------------------------------------

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

        await Click(Btn(page, $"Edit Comment from {a.Date}"), page);
        await Dismiss(page);
        Assert.IsFalse(page.HasConfirmation());
        Assert.IsFalse(page.HasFormDialog());

        await DetailsButton(page).ClickAsync(new());
        page.WaitForAssertion(() => Assert.HasCount(2, Items(page)));
        await Click(Btn(page, $"Edit Comment from {a.Date}"), page);
        await EditBox(page)!.InputAsync(new ChangeEventArgs { Value = "Changed" });
        await Dismiss(page);

        Assert.IsTrue(page.HasConfirmation());
        await page.ConfirmAsync("Discard");
        Assert.IsFalse(page.HasFormDialog());
        Assert.IsEmpty(api.Requests.Where(r => r.Method != HttpMethod.Get).ToList());
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private string OnePath(Stored c) => $"{listPath}/{c.Id}";

    // The API's JSON for one Comment, with its eTag in the body as the API sends it.
    private object Json(Stored c) => new { id = c.Id, activityId = race.Id, text = c.Text, createdUtc = c.Added, eventId = (Guid?)null, eTag = c.Tag };

    private HttpResponseMessage ListResponse(params Stored[] comments) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(comments.Select(Json).ToArray()) };

    private void List(params Stored[] comments) => api.On(HttpMethod.Get, listPath, () => ListResponse(comments));

    private HttpResponseMessage One(Stored c, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = JsonContent.Create(Json(c)) };
        response.Headers.TryAddWithoutValidation("ETag", c.Tag);
        return response;
    }

    // Renders the page and waits for the Activities and the three lists the page reads.
    private IRenderedComponent<Activities> Open()
    {
        var page = harness.Render<Activities>();
        page.WaitForAssertion(() => Assert.AreEqual(4, api.Requests.Count(r => r.Method == HttpMethod.Get)));
        page.WaitForRows(1);
        page.WaitForAssertion(() => Assert.IsFalse(page.Rows().Any(r => r.Contains('—'))));
        return page;
    }

    private async Task<IRenderedComponent<Activities>> OpenDetails()
    {
        var page = Open();
        await DetailsButton(page).ClickAsync(new());
        page.WaitForAssertion(() => Assert.IsNotNull(NewBox(page)));
        return page;
    }

    private static IElement DetailsButton(IRenderedComponent<Activities> page) =>
        page.FindAll("tbody tr.fluent-data-grid-row").Single(r => r.Closest("fluent-dialog") is null)
            .QuerySelector("fluent-button[title=Details]")!;

    private static List<IElement> Items(IRenderedComponent<Activities> page) =>
        page.FormDialog().QuerySelectorAll("li.comment").ToList();

    private static List<string> Texts(IRenderedComponent<Activities> page) =>
        Items(page).Select(i => (i.QuerySelector(".comment-text")?.TextContent ?? "(editing)")).ToList();

    private static IElement? NewBox(IRenderedComponent<Activities> page) =>
        page.FormDialog().QuerySelector("fluent-text-area[aria-label='New Comment']");

    private static IElement? EditBox(IRenderedComponent<Activities> page) =>
        page.FormDialog().QuerySelector("fluent-text-area[aria-label^='Edit Comment from']");

    private static IElement Btn(IRenderedComponent<Activities> page, string label) =>
        page.FormDialog().QuerySelectorAll("fluent-button").Single(x => x.GetAttribute("aria-label") == label);

    private static Task TypeNew(IRenderedComponent<Activities> page, string text) =>
        NewBox(page)!.InputAsync(new ChangeEventArgs { Value = text });

    // A click whose request finishes at once; waits for the render after it.
    private static async Task Click(IElement button, IRenderedComponent<Activities> page)
    {
        await button.ClickAsync(new());
        page.WaitForAssertion(() => Assert.IsFalse(page.FormDialog().Button("Close").IsBusy()));
    }

    // Overlay click or Escape on the details dialog.
    private static Task Dismiss(IRenderedComponent<Activities> page)
    {
        var dialog = page.FormDialog();
        return dialog.TriggerEventAsync("ondialogdismiss", new DialogEventArgs { Id = dialog.Id, Reason = "dismiss" });
    }
}
