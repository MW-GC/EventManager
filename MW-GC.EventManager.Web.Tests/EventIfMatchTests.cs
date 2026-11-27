using System.Net;
using Azure;
using Bunit;
using Microsoft.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// #204 story 22, #268 item 2: an Event save or delete sends back the Event's eTag as the page read it, as If-Match
// (as read: Azure's weak tags are not .NET-parsed), so an Event another admin saved meanwhile is the API's 409, shown
// through the existing message; an Event read with no tag sends no If-Match, as before. The edit dialog's Update Event
// (a standalone Event, and This event only on an Occurrence) is tested here over OccurrenceEditFixture; Delete, an
// Occurrence's cancel and Select Winner in EventDeleteAndWinnerIfMatchTests below.
[TestClass]
public sealed class EventEditIfMatchTests : OccurrenceEditFixture
{
    private const string QuizTag = "W/\"datetime'2026-11-26T08%3A00%3A00.0000000Z'\"";
    private const string RaidEventTag = "W/\"datetime'2026-11-26T09%3A15%3A00.0000000Z'\"";

    [TestMethod]
    public async Task AStandaloneEventsUpdate_SendsTheTagTheListRead_AsIfMatch()
    {
        quiz.ETag = new ETag(QuizTag);
        var page = await OpenEdit("Quiz night");
        await Rename(page, "Quiz finals");

        await Update(page);

        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        CollectionAssert.AreEqual(new[] { QuizTag }, api.IfMatches(HttpMethod.Put, EventPath(quiz)));
    }

    [TestMethod]
    public async Task AnEventTheListReadWithNoTag_IsUpdatedWithNoIfMatch()
    {
        var page = await OpenEdit("Quiz night");
        await Rename(page, "Quiz finals");

        await Update(page);

        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        CollectionAssert.AreEqual(new string?[] { null }, api.IfMatches(HttpMethod.Put, EventPath(quiz)));
    }

    // This event only is the Event route: it sends the Occurrence's own tag, never its Series' (RaidTag).
    [TestMethod]
    public async Task ThisEventOnly_SendsTheOccurrencesOwnTag_AsIfMatch()
    {
        raid.ETag = new ETag(RaidEventTag);
        var page = await OpenEdit("Weekly raid");
        await Rename(page, "Raid night");
        await Update(page);

        await ChooseAndConfirm(page, "This event only");

        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        CollectionAssert.AreEqual(new[] { RaidEventTag }, api.IfMatches(HttpMethod.Put, EventPath(raid)));
    }

    [TestMethod]
    public async Task AStaleUpdate_Is409_AndTheDialogStaysOpen_WithTheConflictMessage()
    {
        quiz.ETag = new ETag(QuizTag);
        api.Status(HttpMethod.Put, EventPath(quiz), HttpStatusCode.Conflict, ConflictBody.Json);
        var page = await OpenEdit("Quiz night");
        await Rename(page, "Quiz finals");

        await Update(page);

        page.WaitForAssertion(() => Assert.AreEqual($"The event was not saved. {ConflictBody.Text}", Assert.ContainsSingle(EditDialog(page).Alerts())));
        CollectionAssert.AreEqual(new[] { QuizTag }, api.IfMatches(HttpMethod.Put, EventPath(quiz)));
    }
}

// Delete in the Events list (a standalone Event, and an Occurrence's two cancel scopes) and Select Winner in the
// details dialog. The Series cancel runs from the list row, which holds no Series tag, so it sends none, as before.
[TestClass]
public sealed class EventDeleteAndWinnerIfMatchTests : IDisposable
{
    private const string QuizTag = "W/\"datetime'2026-11-26T08%3A00%3A00.0000000Z'\"";
    private const string RaidTag = "W/\"datetime'2026-11-26T09%3A15%3A00.0000000Z'\"";
    private const string OpenTag = "W/\"datetime'2026-11-26T10%3A30%3A00.0000000Z'\"";
    private static readonly Guid RaidSeries = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly GameEntity kart = new() { Name = "Kart" };
    private readonly ActivityEntity grandPrix, timeTrial;
    private readonly EventEntity quiz, raid, open;

    public EventDeleteAndWinnerIfMatchTests()
    {
        grandPrix = new ActivityEntity { Name = "Grand Prix", GameId = kart.Id };
        timeTrial = new ActivityEntity { Name = "Time Trial", GameId = kart.Id };
        quiz = new EventEntity { Name = "Quiz night", Date = new DateTimeOffset(2026, 10, 1, 19, 0, 0, TimeSpan.Zero), Selections = [Pick(kart, grandPrix)], ETag = new ETag(QuizTag) };
        var key = new DateTimeOffset(2026, 10, 15, 19, 0, 0, TimeSpan.Zero);
        raid = new EventEntity { Name = "Weekly raid", Date = key, SeriesId = RaidSeries, OccurrenceKey = key, ETag = new ETag(RaidTag) };
        open = new EventEntity
        {
            Name = "Open night", Date = new DateTimeOffset(2026, 10, 16, 19, 0, 0, TimeSpan.Zero), UniqueGamesOnly = false,
            Selections = [Pick(kart, grandPrix), Pick(kart, timeTrial)], ETag = new ETag(OpenTag)
        };
        api.Json(HttpMethod.Get, "/api/games", new[] { kart })
           .Json(HttpMethod.Get, "/api/activities", new[] { grandPrix, timeTrial })
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>())
           .Json(HttpMethod.Get, "/api/events", new[] { quiz, raid, open })
           .Status(HttpMethod.Delete, EventPath(quiz), HttpStatusCode.NoContent)
           .Status(HttpMethod.Delete, EventPath(raid), HttpStatusCode.NoContent)
           .Status(HttpMethod.Delete, SeriesPath, HttpStatusCode.NoContent)
           .Status(HttpMethod.Put, EventPath(open), HttpStatusCode.OK, "{}");
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    [TestMethod]
    public async Task ADelete_SendsTheRowsTag_AsIfMatch()
    {
        var page = Loaded();

        await RowButton(page, "Quiz night", "Delete").ClickAsync(new());
        await page.ConfirmAsync();

        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { QuizTag }, api.IfMatches(HttpMethod.Delete, EventPath(quiz))));
    }

    [TestMethod]
    public async Task ThisEventOnlyCancel_SendsTheOccurrencesTag_AsIfMatch()
    {
        var page = Loaded();

        await RowButton(page, "Weekly raid", "Delete").ClickAsync(new());
        await ChooseAndConfirm(page, "This event only");

        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { RaidTag }, api.IfMatches(HttpMethod.Delete, EventPath(raid))));
    }

    [TestMethod]
    public async Task TheSeriesCancel_SendsNoIfMatch()
    {
        var page = Loaded();

        await RowButton(page, "Weekly raid", "Delete").ClickAsync(new());
        await ChooseAndConfirm(page, "This and all future events");

        page.WaitForAssertion(() => CollectionAssert.AreEqual(new string?[] { null }, api.IfMatches(HttpMethod.Delete, SeriesPath)));
        Assert.AreEqual(0, api.Count(HttpMethod.Delete, EventPath(raid)));
    }

    [TestMethod]
    public async Task AStaleDelete_Is409_ThePageShowsTheConflictMessage_AndKeepsTheRow()
    {
        api.Status(HttpMethod.Delete, EventPath(quiz), HttpStatusCode.Conflict, ConflictBody.Json);
        var page = Loaded();

        await RowButton(page, "Quiz night", "Delete").ClickAsync(new());
        await page.ConfirmAsync();

        page.WaitForAssertion(() => Assert.AreEqual($"The event was not deleted. {ConflictBody.Text}", Assert.ContainsSingle(page.PageAlerts())));
        Assert.IsTrue(page.Rows().Any(r => r.Contains("Quiz night")), "the row went although the delete was refused");
        CollectionAssert.AreEqual(new[] { QuizTag }, api.IfMatches(HttpMethod.Delete, EventPath(quiz)));
    }

    [TestMethod]
    public async Task SelectWinner_SendsTheTagTheDetailsDialogOpenedWith_AsIfMatch()
    {
        var page = Loaded();
        await RowButton(page, "Open night", "Details").ClickAsync(new());

        await Card(page, "Time Trial").Button("Select Winner").ClickAsync(new());

        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { OpenTag }, api.IfMatches(HttpMethod.Put, EventPath(open))));
    }

    [TestMethod]
    public async Task AStaleSelectWinner_Is409_AndTheDialogShowsTheConflictMessage()
    {
        api.Status(HttpMethod.Put, EventPath(open), HttpStatusCode.Conflict, ConflictBody.Json);
        var page = Loaded();
        await RowButton(page, "Open night", "Details").ClickAsync(new());

        await Card(page, "Time Trial").Button("Select Winner").ClickAsync(new());

        page.WaitForAssertion(() => Assert.Contains($"The winner was not saved. {ConflictBody.Text}", page.FormDialog().Alerts()));
        Assert.AreEqual("Select Winner", Card(page, "Time Trial").QuerySelector("fluent-button")?.TextContent.Trim(), "the refused Winner shows as won");
        CollectionAssert.AreEqual(new[] { OpenTag }, api.IfMatches(HttpMethod.Put, EventPath(open)));
    }

    private static string EventPath(EventEntity e) => $"/api/events/{e.Id}";
    private static readonly string SeriesPath = $"/api/series/{RaidSeries}";

    private IRenderedComponent<Events> Loaded()
    {
        var page = harness.Render<Events>();
        page.WaitForRows(3);
        return page;
    }

    // Chooses the scope whose visible label is this, as the radio group reports a choice, then confirms it.
    private static async Task ChooseAndConfirm(IRenderedComponent<Events> page, string label)
    {
        var question = page.Find("fluent-dialog.scope-dialog");
        var radio = question.QuerySelectorAll("fluent-radio").Single(r => r.QuerySelector(".scope-label")!.TextContent.Trim() == label);
        await question.QuerySelector("fluent-radio-group")!.TriggerEventAsync("onradiogroupchange", new ChangeEventArgs { Value = radio.GetAttribute("current-value") });
        await page.Find("fluent-dialog.scope-dialog fluent-button.scope-confirm").ClickAsync(new());
    }
}

// The API's 409 for a stale If-Match (EntityResults.Conflict): this text, as a JSON string.
internal static class ConflictBody
{
    public const string Text = "The request conflicts with the stored data. Reload and try again.";
    public const string Json = $"\"{Text}\"";
}
