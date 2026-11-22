using System.Net;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using static MW_GC.EventManager.Web.Tests.CommentsPanelView;

namespace MW_GC.EventManager.Web.Tests;

// #132: the Events page and the Activities page over a stub API that stores each Activity's Comments, for the tests
// of the Comments panel in the Event details dialog (CommentsFromEventDetailsTests, CommentsSelectWinnerTests).
public abstract class CommentsFromEventDetailsFixture : IDisposable
{
    // Azure Table Storage tags are weak and carry quotes; they must reach If-Match as read.
    protected const string TagA = "W/\"datetime'2026-11-20T10%3A00%3A00.0000000Z'\"";
    protected const string TagA2 = "W/\"datetime'2026-11-20T10%3A30%3A00.0000000Z'\"";
    protected const string TagB = "W/\"datetime'2026-11-20T11%3A00%3A00.0000000Z'\"";
    protected const string TagC = "W/\"datetime'2026-11-20T12%3A00%3A00.0000000Z'\"";
    // Noon UTC, so the local date is the same day in every time zone the tests may run in.
    protected static readonly DateTimeOffset Added = new(2025, 3, 14, 12, 0, 0, TimeSpan.Zero);

    internal readonly StubApiHandler api = new();
    internal readonly PageHarness harness;
    protected readonly GameEntity kart = new() { Name = "Kart" };
    protected readonly ActivityEntity grandPrix;
    protected readonly ActivityEntity timeTrial;
    // Won by Grand Prix.
    protected readonly EventEntity night;
    // Two Selections and no Winner yet.
    protected readonly EventEntity open;
    // What the stub API stores per Activity: the Comments rows, oldest first.
    internal readonly Dictionary<Guid, List<StoredComment>> comments = [];

    protected CommentsFromEventDetailsFixture()
    {
        grandPrix = new ActivityEntity { Name = "Grand Prix", GameId = kart.Id };
        timeTrial = new ActivityEntity { Name = "Time Trial", GameId = kart.Id };
        night = new EventEntity
        {
            Name = "Game night",
            Date = new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero),
            UniqueGamesOnly = false,
            Selections = [PageActions.Pick(kart, grandPrix), PageActions.Pick(kart, timeTrial)],
            WinnerActivityId = grandPrix.Id
        };
        open = new EventEntity
        {
            Name = "Open night",
            Date = new DateTimeOffset(2026, 10, 16, 19, 0, 0, TimeSpan.Zero),
            UniqueGamesOnly = false,
            Selections = [PageActions.Pick(kart, grandPrix), PageActions.Pick(kart, timeTrial)]
        };
        api.Json(HttpMethod.Get, "/api/games", new[] { kart })
           .Json(HttpMethod.Get, "/api/activities", new[] { grandPrix, timeTrial })
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>())
           .Json(HttpMethod.Get, "/api/events", new[] { night, open })
           // The Activity details dialog's Performance section (#137) reads these too; answered so it loads quietly.
           .Json(HttpMethod.Get, "/api/event-metrics", Array.Empty<object>());
        foreach (var activity in new[] { grandPrix, timeTrial })
        {
            var id = activity.Id;
            comments[id] = [];
            api.On(HttpMethod.Get, ListPath(id), () => StoredComment.List(id, comments[id]));
        }
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    // A Comment row as the stub stores it, added at noon UTC.
    internal static StoredComment Comment(string text, Guid? eventId, string tag) => new(Guid.NewGuid(), text, Added, eventId, tag);

    protected static string ListPath(Guid activityId) => $"/api/activities/{activityId}/comments";

    protected static string OnePath(Guid activityId, Guid commentId) => $"{ListPath(activityId)}/{commentId}";

    protected static EventEntity Rewon(EventEntity e, ActivityEntity winner) => new()
    {
        Id = e.Id, Name = e.Name, Date = e.Date, UniqueGamesOnly = e.UniqueGamesOnly, Selections = e.Selections, WinnerActivityId = winner.Id
    };

    // An add, stored as the API would store it: the text and the Event id the request sent.
    protected HttpResponseMessage StoreAdd(Guid activityId)
    {
        using var body = JsonDocument.Parse(api.Bodies(HttpMethod.Post, ListPath(activityId)).Last()!);
        Guid? eventId = body.RootElement.TryGetProperty("eventId", out var id) ? id.GetGuid() : null;
        var stored = Comment(body.RootElement.GetProperty("text").GetString()!, eventId, TagC);
        comments[activityId].Add(stored);
        return stored.Response(activityId, HttpStatusCode.Created);
    }

    protected async Task<IRenderedComponent<Events>> OpenEventDetails(string name)
    {
        api.On(HttpMethod.Post, ListPath(grandPrix.Id), () => StoreAdd(grandPrix.Id));
        var page = harness.Render<Events>();
        page.WaitForRows(2);
        await EventDetailsButton(page, name).ClickAsync(new());
        Assert.IsTrue(page.HasFormDialog());
        if (name == night.Name) page.WaitForAssertion(() => Assert.IsNotNull(NewBox(page)));
        return page;
    }

    protected IRenderedComponent<Activities> OpenActivities()
    {
        var page = harness.Render<Activities>();
        page.WaitForRows(2);
        page.WaitForAssertion(() => Assert.IsFalse(page.Rows().Any(r => r.Contains('—'))));
        return page;
    }

    protected static IElement EventDetailsButton(IRenderedComponent<Events> page, string name) => PageActions.RowButton(page, name, "Details");

    protected static IElement ActivityDetailsButton(IRenderedComponent<Activities> page, string name) => PageActions.RowButton(page, name, "Details");

    // The Selection card of one Activity in the Event details dialog.
    protected static IElement Card(IRenderedComponent<Events> page, string activity) => PageActions.Card(page, activity);

    protected static List<IElement> Panels<T>(IRenderedComponent<T> page) where T : IComponent =>
        page.FormDialog().QuerySelectorAll(".comments").ToList();

    // The origin beside a Comment's date, or null for a Comment that names no Event.
    protected static string? Origin(IElement item) => item.QuerySelector(".comment-origin")?.TextContent.Trim();

    protected static List<string> Origins<T>(IRenderedComponent<T> page) where T : IComponent =>
        Items(page).Select(Origin).OfType<string>().ToList();
}
