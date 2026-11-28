using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using AngleSharp.Dom;
using Bunit;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using static MW_GC.EventManager.Web.Tests.CommentsPanelView;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// #131: the Comments panel in the Activity details dialog, rendered with bUnit through the Activities page
// against a stub API. Every Comment of the Activity shows in the API's order with its added date, and an
// admin adds, edits and deletes them one at a time. Each Comment is its own row with its own eTag
// (ADR 0001): an edit or delete sends that Comment's tag as If-Match, so two Comments never conflict, and a
// stale tag on one Comment shows the stale-edit message beside it with Reload. The tests are split by
// behaviour: CommentsPanelTests (the list), CommentsPanelAddTests, CommentsPanelEditAndDeleteTests,
// CommentsPanelStaleEditTests, CommentsPanelBusyGuardTests and CommentsPanelDiscardTests.
public abstract class CommentsPanelFixture : IDisposable
{
    // Azure Table Storage tags are weak and carry quotes; they must reach If-Match as read.
    protected const string TagA = "W/\"datetime'2026-11-20T10%3A00%3A00.0000000Z'\"";
    protected const string TagA2 = "W/\"datetime'2026-11-20T10%3A30%3A00.0000000Z'\"";
    protected const string TagB = "W/\"datetime'2026-11-20T11%3A00%3A00.0000000Z'\"";
    protected const string TagC = "W/\"datetime'2026-11-20T12%3A00%3A00.0000000Z'\"";
    protected const string StaleMessage = "This Comment was changed by someone else. Reload and try again.";
    protected const string NotFound = "Not found. It may have been deleted.";
    protected const string Required = "Comment text is required.";
    protected const string TooLong = "Comment text must be 1000 characters or fewer.";
    protected const string ServerError = "The server had a problem (HTTP 500). Try again.";
    protected const string Unreachable = "Could not reach the server. Check the connection and try again.";

    internal readonly StubApiHandler api = new();
    internal readonly PageHarness harness;
    protected readonly GameEntity kart = new() { Name = "Kart" };
    protected readonly ActivityEntity race;
    protected readonly string listPath;
    // Some tests pin the culture of the shown date; the next test gets the one it started with.
    private readonly CultureInfo culture = CultureInfo.CurrentCulture;
    // Added at noon local time and sent in UTC, as the API sends it. The panel shows local time, so the date and
    // time that name each Comment's buttons are these literals in every time zone.
    protected readonly Stored a = new(Guid.NewGuid(), "Ran long", AddedAtLocal(2025, 3, 14, 12, 0), TagA);
    protected readonly Stored b = new(Guid.NewGuid(), "Crowd favourite", AddedAtLocal(2025, 6, 1, 12, 0), TagB);
    protected const string AAdded = "2025-03-14 12:00";
    protected const string BAdded = "2025-06-01 12:00";

    protected sealed record Stored(Guid Id, string Text, DateTimeOffset Added, string Tag);

    protected CommentsPanelFixture()
    {
        race = new ActivityEntity { Name = "Race", GameId = kart.Id, Description = "Three laps" };
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

    public void Dispose()
    {
        CultureInfo.CurrentCulture = culture;
        harness.Dispose();
    }

    protected string OnePath(Stored c) => $"{listPath}/{c.Id}";

    // The API's JSON for one Comment, with its eTag in the body as the API sends it.
    private object Json(Stored c) => new { id = c.Id, activityId = race.Id, text = c.Text, createdUtc = c.Added, eventId = (Guid?)null, eTag = c.Tag };

    protected HttpResponseMessage ListResponse(params Stored[] comments) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(comments.Select(Json).ToArray()) };

    protected void List(params Stored[] comments) => api.On(HttpMethod.Get, listPath, () => ListResponse(comments));

    // The API's answer to an add or edit: the stored Comment, with its eTag in the body and the header.
    protected HttpResponseMessage CommentResponse(Stored c, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = JsonContent.Create(Json(c)) };
        response.Headers.TryAddWithoutValidation("ETag", c.Tag);
        return response;
    }

    // Renders the page and waits for the Activities and the three lists the page reads.
    protected IRenderedComponent<Activities> Open()
    {
        var page = harness.Render<Activities>();
        page.WaitForAssertion(() => Assert.AreEqual(4, api.Requests.Count(r => r.Method == HttpMethod.Get)));
        page.WaitForRows(1);
        page.WaitForAssertion(() => Assert.IsFalse(page.Rows().Any(r => r.Contains('—'))));
        return page;
    }

    protected async Task<IRenderedComponent<Activities>> OpenDetails()
    {
        var page = Open();
        await DetailsButton(page).ClickAsync(new());
        page.WaitForAssertion(() => Assert.IsNotNull(NewBox(page)));
        return page;
    }

    protected IElement DetailsButton(IRenderedComponent<Activities> page) => RowButton(page, race.Name, "Details");
}
