using System.Globalization;
using System.Net;
using Bunit;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Pages;
using static MW_GC.EventManager.Web.Tests.CommentsPanelView;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// The Activity details dialog on the Activities page as a whole: its tags, how its Comments are named, what a
// delete of a Comment answered with 204 does, and the one Events read it shares between its Performance and Comments
// panels (#179, #186). The panels on their own are covered in CommentsPanelTests and ActivityPerformancePanelTests.
[TestClass]
public sealed class ActivityDetailsDialogTests : IDisposable
{
    private const string TagA = "W/\"datetime'2026-11-20T14%3A05%3A00.0000000Z'\"";
    private const string TagB = "W/\"datetime'2026-11-20T16%3A30%3A00.0000000Z'\"";
    private const string EventsPath = "/api/events";

    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly GameEntity kart = new() { Name = "Kart" };
    private readonly ThemeEntity spooky = new() { Name = "Spooky" };
    private readonly ActivityEntity race;
    private readonly EventEntity night;
    private readonly string listPath;

    public ActivityDetailsDialogTests()
    {
        race = new ActivityEntity { Name = "Race", GameId = kart.Id, ThemeIds = [spooky.Id] };
        night = new EventEntity
        {
            Name = "Game night",
            Date = new DateTimeOffset(2020, 3, 1, 19, 0, 0, TimeSpan.Zero),
            UniqueGamesOnly = false,
            Selections = [Pick(kart, race)],
            WinnerActivityId = race.Id
        };
        listPath = $"/api/activities/{race.Id}/comments";
        api.Json(HttpMethod.Get, "/api/games", new[] { kart })
           .Json(HttpMethod.Get, "/api/activities", new[] { race })
           .Json(HttpMethod.Get, "/api/themes", new[] { spooky })
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>())
           .Json(HttpMethod.Get, EventsPath, new[] { night })
           .Json(HttpMethod.Get, "/api/event-metrics", Array.Empty<object>());
        List();
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    // #179 item 8: the dialog still shows the Activity's Theme as a tag, by name.
    [TestMethod]
    public async Task Details_AnActivityWithATheme_ShowsTheThemeTagByName()
    {
        var page = await OpenDetails();

        var tags = page.FormDialog().QuerySelectorAll("fluent-badge.tag-badge").Select(b => Normalize(b.TextContent)).ToList();
        Assert.AreEqual("Theme: Spooky", Assert.ContainsSingle(tags));
    }

    // #179 item 7: two Comments added on the same day are told apart by their time, in their buttons' names and in
    // the Delete question.
    [TestMethod]
    public async Task TwoCommentsAddedOnOneDay_HaveDifferentNames_AndDifferentDeleteQuestions()
    {
        var first = new StoredComment(Guid.NewGuid(), "Ran long", AddedAtLocal(2026, 11, 20, 14, 5), null, TagA);
        var second = new StoredComment(Guid.NewGuid(), "Crowd favourite", AddedAtLocal(2026, 11, 20, 16, 30), null, TagB);
        List(first, second);
        var page = await OpenDetails();

        var names = Items(page).Select(i => i.QuerySelectorAll("fluent-button").Select(b => b.GetAttribute("aria-label")).ToList()).ToList();
        CollectionAssert.AreEqual(new[] { "Edit Comment from 2026-11-20 14:05", "Delete Comment from 2026-11-20 14:05" }, names[0]);
        CollectionAssert.AreEqual(new[] { "Edit Comment from 2026-11-20 16:30", "Delete Comment from 2026-11-20 16:30" }, names[1]);

        await Click(ButtonNamed(page, "Delete Comment from 2026-11-20 16:30"), page);
        Assert.AreEqual("Delete Comment from 2026-11-20 16:30?", page.Confirmation().QuerySelector(".confirm-message")!.TextContent.Trim());
        await page.Confirmation().Button("Cancel").ClickAsync(new());
        await Click(ButtonNamed(page, "Delete Comment from 2026-11-20 14:05"), page);
        Assert.AreEqual("Delete Comment from 2026-11-20 14:05?", page.Confirmation().QuerySelector(".confirm-message")!.TextContent.Trim());
    }

    // #200.8: the shown date and the name come from the browser's local time, pinned here under a zone that is never
    // UTC (Nepal, UTC+05:45), so dropping ToLocalTime() fails even on a machine or CI runner set to UTC. 20:30 UTC
    // on 20 November is 02:15 on 21 November there: both the date and the time move.
    [TestMethod]
    public async Task TheDateAndTheName_AreInTheBrowsersLocalTime_NotUtc()
    {
        var culture = CultureInfo.CurrentCulture;
        var zone = Environment.GetEnvironmentVariable("TZ");
        try
        {
            Environment.SetEnvironmentVariable("TZ", "Asia/Kathmandu");
            TimeZoneInfo.ClearCachedData();
            if (TimeZoneInfo.Local.BaseUtcOffset != new TimeSpan(5, 45, 0))
                Assert.Inconclusive("This platform does not take the local zone from TZ.");
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            List(new StoredComment(Guid.NewGuid(), "Late finish", new DateTimeOffset(2026, 11, 20, 20, 30, 0, TimeSpan.Zero), null, TagA));
            var page = await OpenDetails();

            var item = Assert.ContainsSingle(Items(page));
            Assert.AreEqual("11/21/2026", item.QuerySelector("time")!.TextContent.Trim());
            Assert.AreEqual("2026-11-20T20:30:00Z", item.QuerySelector("time")!.GetAttribute("datetime"));
            Assert.IsNotNull(ButtonNamed(page, "Edit Comment from 2026-11-21 02:15"));
            Assert.IsNotNull(ButtonNamed(page, "Delete Comment from 2026-11-21 02:15"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TZ", zone);
            TimeZoneInfo.ClearCachedData();
            CultureInfo.CurrentCulture = culture;
        }
    }

    // #179 item 10: the API answers a delete with 204, also for a Comment someone else already deleted, so the client
    // cannot tell the two apart. #200.5: the name says only what this shows: the row goes, the other stays, with no
    // message and no second read of the list.
    [TestMethod]
    public async Task ADeleteAnsweredWith204_RemovesOnlyThatRow_WithoutAMessageOrASecondListRead()
    {
        var gone = new StoredComment(Guid.NewGuid(), "Deleted elsewhere", AddedAtLocal(2026, 11, 20, 14, 5), null, TagA);
        var kept = new StoredComment(Guid.NewGuid(), "Still here", AddedAtLocal(2026, 11, 20, 16, 30), null, TagB);
        List(gone, kept);
        api.Status(HttpMethod.Delete, $"{listPath}/{gone.Id}", HttpStatusCode.NoContent);
        var page = await OpenDetails();

        await Click(ButtonNamed(page, "Delete Comment from 2026-11-20 14:05"), page);
        await page.ConfirmAsync();
        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { "Still here" }, Texts(page)));

        Assert.AreEqual(TagA, Assert.ContainsSingle(api.IfMatches(HttpMethod.Delete, $"{listPath}/{gone.Id}")));
        Assert.IsEmpty(page.FormDialog().Alerts());
        Assert.AreEqual(1, api.Count(HttpMethod.Get, listPath));
    }

    // #186 item 12: the dialog reads the Events list once, for both the Performance panel and the Event names
    // of the Comments panel.
    [TestMethod]
    public async Task Details_ReadTheEventsListOnce_ForPerformanceAndCommentOrigins()
    {
        List(new StoredComment(Guid.NewGuid(), "Ran long", AddedAtLocal(2026, 11, 20, 14, 5), night.Id, TagA));
        var page = await OpenDetails();

        page.WaitForAssertion(() => Assert.AreEqual("from Game night", Items(page)[0].QuerySelector(".comment-origin")?.TextContent.Trim()));
        page.WaitForAssertion(() => Assert.Contains("Offered in 1 Event", Normalize(page.FormDialog().QuerySelector(".activity-performance")!.TextContent)));
        Assert.AreEqual(1, api.Count(HttpMethod.Get, EventsPath));

        // A new opening reads it again, once.
        await page.FormDialog().Button("Close").ClickAsync(new());
        await OpenAgain(page);
        page.WaitForAssertion(() => Assert.AreEqual("from Game night", Items(page)[0].QuerySelector(".comment-origin")?.TextContent.Trim()));
        Assert.AreEqual(2, api.Count(HttpMethod.Get, EventsPath));
    }

    // The shared read failed: Performance says so with Retry, and Retry reads the Events list again. A failed read is
    // not kept, so the Comments panel, asking after it failed, sends its own (#200.6): the Comments read is held until
    // Performance shows the failure, so that order is fixed and every count is exact.
    [TestMethod]
    public async Task ASharedEventsReadThatFailed_IsReadAgainByPerformanceRetry()
    {
        var comments = api.Hold(HttpMethod.Get, listPath, () =>
            StoredComment.List(race.Id, [new StoredComment(Guid.NewGuid(), "Ran long", AddedAtLocal(2026, 11, 20, 14, 5), night.Id, TagA)]));
        api.Status(HttpMethod.Get, EventsPath, HttpStatusCode.InternalServerError);
        var page = harness.Render<Activities>();
        page.WaitForRows(1);
        await RowButton(page, "Race", "Details").ClickAsync(new());
        var performance = () => page.FormDialog().QuerySelector(".activity-performance")!;
        page.WaitForAssertion(() => Assert.Contains("Performance could not be loaded. Events could not be loaded.", performance().TextContent));
        Assert.AreEqual(1, api.Count(HttpMethod.Get, EventsPath), "Performance's read");

        comments.SetResult();
        page.WaitForAssertion(() => Assert.AreEqual("from an Event", Items(page)[0].QuerySelector(".comment-origin")?.TextContent.Trim()));
        Assert.AreEqual(2, api.Count(HttpMethod.Get, EventsPath), "the Comments panel's read, after the first failed");

        api.Json(HttpMethod.Get, EventsPath, new[] { night });
        await performance().Button("Retry").ClickAsync(new());

        page.WaitForAssertion(() => Assert.Contains("Offered in 1 Event", Normalize(performance().TextContent)));
        Assert.AreEqual(3, api.Count(HttpMethod.Get, EventsPath), "Retry's read");
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private void List(params StoredComment[] comments) => api.On(HttpMethod.Get, listPath, () => StoredComment.List(race.Id, comments));

    private async Task<IRenderedComponent<Activities>> OpenDetails()
    {
        var page = harness.Render<Activities>();
        page.WaitForRows(1);
        page.WaitForAssertion(() => Assert.IsFalse(page.Rows().Any(r => r.Contains('—'))));
        await OpenAgain(page);
        return page;
    }

    private static async Task OpenAgain(IRenderedComponent<Activities> page)
    {
        await RowButton(page, "Race", "Details").ClickAsync(new());
        page.WaitForAssertion(() => Assert.IsNotNull(NewBox(page)));
    }
}
