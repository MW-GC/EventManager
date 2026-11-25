using System.Net;
using System.Text;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// #100, #204 (Web part 2a, stories 17, 18, 21): Delete on an Occurrence in the Events list asks which scope to cancel,
// this one Occurrence (DELETE api/events/{id}) or its whole Series from now on (DELETE api/series/{seriesId}?scope=all);
// a standalone Event keeps the plain question and route. The stub API stores the Events: an Event delete removes that
// row, a Series cancel removes every row of that Series, and every list read returns what is stored. The query string
// (?scope=all) is pinned in SeriesServiceTests and the route table, since the stub matches on the path.
[TestClass]
public sealed class OccurrenceDeleteScopeTests : IDisposable
{
    private static readonly Guid RaidSeries = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid CupSeries = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly EventEntity quiz = Night("Quiz night", 1, null);
    private readonly EventEntity raid = Night("Weekly raid", 2, RaidSeries);
    private readonly EventEntity nextRaid = Night("Next raid", 9, RaidSeries);
    private readonly EventEntity cup = Night("Monthly cup", 3, CupSeries);
    private readonly List<EventEntity> stored;

    public OccurrenceDeleteScopeTests()
    {
        stored = [quiz, raid, nextRaid, cup];
        api.Json(HttpMethod.Get, "/api/games", Array.Empty<GameEntity>())
           .Json(HttpMethod.Get, "/api/activities", Array.Empty<ActivityEntity>())
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>())
           .On(HttpMethod.Get, "/api/events", () => Answer(stored));
        foreach (var e in stored.ToList())
            api.On(HttpMethod.Delete, EventPath(e), () => Removed(x => x.Id == e.Id));
        foreach (var series in new[] { RaidSeries, CupSeries })
            api.On(HttpMethod.Delete, SeriesPath(series), () => Removed(x => x.SeriesId == series));
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    [TestMethod]
    public async Task AStandaloneEventsDelete_AsksThePlainQuestion_AndDeletesOnlyThatEvent()
    {
        var page = Loaded();

        await RowButton(page, "Quiz night", "Delete").ClickAsync(new());

        Assert.IsFalse(HasScopeQuestion(page), "a standalone Event was asked the scope question");
        Assert.AreEqual("Delete event", page.Confirmation().GetAttribute("aria-label"));
        Assert.AreEqual("Delete Quiz night? This cannot be undone.", page.Confirmation().QuerySelector(".confirm-message")!.TextContent.Trim());
        await page.ConfirmAsync();

        page.WaitForAssertion(() => Assert.IsFalse(page.Rows().Any(r => r.Contains("Quiz night"))));
        CollectionAssert.AreEqual(new[] { $"DELETE {EventPath(quiz)}" }, Writes());
    }

    [TestMethod]
    public async Task AnOccurrencesDelete_AsksTheScope_WithBothOptions_AndNothingChosen()
    {
        var page = Loaded();

        await RowButton(page, "Weekly raid", "Delete").ClickAsync(new());

        Assert.IsTrue(HasScopeQuestion(page), "an Occurrence's Delete asked no scope question");
        Assert.AreEqual(1, page.FindAll("fluent-dialog").Count, "the plain question opened as well");
        Assert.AreEqual("Delete repeating event", ScopeQuestion(page).GetAttribute("aria-label"));
        CollectionAssert.AreEqual(new[] { "This event only", "This and all future events" }, Labels(page));
        Assert.IsTrue(ConfirmButton(page).HasAttribute("disabled"), "Confirm is enabled before a scope is chosen");
        Assert.IsFalse(ScopeQuestion(page).QuerySelectorAll("fluent-radio").Any(r => r.HasAttribute("checked")), "a scope is chosen already");
        Assert.IsEmpty(Writes());
    }

    [TestMethod]
    public async Task ThisEventOnly_SendsOnlyTheEventDelete_AndOnlyThatRowGoes()
    {
        var page = Loaded();
        await RowButton(page, "Weekly raid", "Delete").ClickAsync(new());

        await Choose(page, "This event only");
        await ConfirmButton(page).ClickAsync(new());

        page.WaitForAssertion(() => Assert.IsFalse(page.Rows().Any(r => r.Contains("Weekly raid"))));
        CollectionAssert.AreEqual(new[] { $"DELETE {EventPath(raid)}" }, Writes());
        Assert.HasCount(3, page.Rows());
        Assert.IsTrue(page.Rows().Any(r => r.Contains("Next raid")), "the rest of the Series went too");
        Assert.IsFalse(HasScopeQuestion(page));
    }

    [TestMethod]
    [DataRow("Weekly raid", "aaaaaaaa-0000-0000-0000-000000000001", new[] { "Quiz night", "Monthly cup" })]
    [DataRow("Monthly cup", "bbbbbbbb-0000-0000-0000-000000000002", new[] { "Quiz night", "Weekly raid", "Next raid" })]
    public async Task ThisAndAllFuture_CancelsTheClickedRowsSeries_AndSendsNoEventDelete(string row, string series, string[] left)
    {
        var page = Loaded();
        await RowButton(page, row, "Delete").ClickAsync(new());

        await Choose(page, "This and all future events");
        await ConfirmButton(page).ClickAsync(new());

        page.WaitForAssertion(() => Assert.HasCount(left.Length, page.Rows()));
        CollectionAssert.AreEqual(new[] { $"DELETE {SeriesPath(Guid.Parse(series))}" }, Writes());
        foreach (var name in left) Assert.IsTrue(page.Rows().Any(r => r.Contains(name)), name + " is gone");
    }

    [TestMethod]
    [DataRow("cancel")]
    [DataRow("dismiss")]
    public async Task CancelOrEscape_SendsNothing_AndReturnsFocusToTheRowsDelete(string how)
    {
        var page = Loaded();
        var delete = RowButton(page, "Weekly raid", "Delete");
        var key = delete.GetAttribute("data-focus-key");
        await delete.ClickAsync(new());
        await Choose(page, "This and all future events");

        if (how == "cancel") await ScopeQuestion(page).Button("Cancel").ClickAsync(new());
        else await ScopeQuestion(page).TriggerEventAsync("ondialogdismiss", new DialogEventArgs { Id = ScopeQuestion(page).Id, Reason = "dismiss" });

        Assert.IsFalse(HasScopeQuestion(page));
        Assert.IsEmpty(Writes());
        Assert.HasCount(4, page.Rows());
        Assert.AreEqual($"delete-{raid.Id}", key);
        Assert.AreEqual(key, Assert.ContainsSingle(harness.JSInterop.VerifyInvoke("emFocus.remember", 1)).Arguments[0]);
        harness.JSInterop.VerifyInvoke("emFocus.restore", 1);
        // The question took focus when it showed and let go of it when it went, as ConfirmDialog does.
        harness.JSInterop.VerifyInvoke("emFocus.question", 1);
        harness.JSInterop.VerifyInvoke("emFocus.answered", 1);
    }

    [TestMethod]
    public async Task AConfirmedScope_RestoresFocusAfterTheRequest()
    {
        var page = Loaded();
        await RowButton(page, "Weekly raid", "Delete").ClickAsync(new());
        await Choose(page, "This event only");
        harness.JSInterop.VerifyNotInvoke("emFocus.restore");

        await ConfirmButton(page).ClickAsync(new());

        page.WaitForAssertion(() => Assert.HasCount(3, page.Rows()));
        harness.JSInterop.VerifyInvoke("emFocus.restore", 1);
    }

    [TestMethod]
    [DataRow("This event only", "The event was not deleted. The server had a problem (HTTP 500). Try again.")]
    [DataRow("This and all future events", "The series was not cancelled. The server had a problem (HTTP 500). Try again.")]
    public async Task AFailedScope_ShowsItsOwnMessage_AndKeepsTheRows(string option, string message)
    {
        var page = Loaded();
        api.Status(HttpMethod.Delete, EventPath(raid), HttpStatusCode.InternalServerError)
           .Status(HttpMethod.Delete, SeriesPath(RaidSeries), HttpStatusCode.InternalServerError);
        await RowButton(page, "Weekly raid", "Delete").ClickAsync(new());

        await Choose(page, option);
        await ConfirmButton(page).ClickAsync(new());

        page.WaitForAssertion(() => Assert.AreEqual(message, Assert.ContainsSingle(page.PageAlerts())));
        Assert.HasCount(4, page.Rows());
        Assert.IsFalse(RowButton(page, "Weekly raid", "Delete").HasAttribute("disabled"), "the row stayed busy after the failure");
        harness.JSInterop.VerifyInvoke("emFocus.restore", 1);
    }

    [TestMethod]
    [DataRow("This event only")]
    [DataRow("This and all future events")]
    public async Task ASuccessfulScope_ReadsTheListAgain(string option)
    {
        var page = Loaded();
        var reads = api.Count(HttpMethod.Get, "/api/events");
        await RowButton(page, "Weekly raid", "Delete").ClickAsync(new());

        await Choose(page, option);
        await ConfirmButton(page).ClickAsync(new());

        page.WaitForAssertion(() =>
        {
            Assert.AreEqual(reads + 1, api.Count(HttpMethod.Get, "/api/events"));
            Assert.IsFalse(page.Rows().Any(r => r.Contains("Weekly raid")), "the list was not read again");
        });
        Assert.IsEmpty(page.PageAlerts());
    }

    [TestMethod]
    [DataRow("This event only")]
    [DataRow("This and all future events")]
    public async Task WhileTheScopeRuns_TheRowIsBusy_AndAnotherDeleteAsksNothing(string option)
    {
        var page = Loaded();
        var path = option == "This event only" ? EventPath(raid) : SeriesPath(RaidSeries);
        var gate = api.Hold(HttpMethod.Delete, path, () => new HttpResponseMessage(HttpStatusCode.NoContent));
        // Another row's Delete, taken while it is enabled: a click that lands before the re-render that disables it.
        var askCup = DeleteHandler(page, "Monthly cup");
        await RowButton(page, "Weekly raid", "Delete").ClickAsync(new());
        await Choose(page, option);

        var confirm = ConfirmButton(page).ClickAsync(new());
        page.WaitForAssertion(() =>
        {
            Assert.IsFalse(HasScopeQuestion(page), "the question closed and the request is running");
            Assert.IsTrue(RowButton(page, "Weekly raid", "Edit").HasAttribute("disabled"), "the row is not busy");
            Assert.IsTrue(RowButton(page, "Monthly cup", "Delete").HasAttribute("disabled"), "another row's Delete is enabled");
        });
        await page.InvokeAsync(() => askCup.InvokeAsync(new()));

        Assert.IsFalse(page.HasConfirmation(), "a second question opened while the request ran");
        gate.SetResult();
        await confirm;
        page.WaitForAssertion(() => Assert.IsFalse(RowButton(page, "Monthly cup", "Delete").HasAttribute("disabled")));
        CollectionAssert.AreEqual(new[] { $"DELETE {path}" }, Writes());
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static EventEntity Night(string name, int day, Guid? series) => new()
    {
        Name = name,
        Date = new DateTimeOffset(2026, 10, day, 19, 0, 0, TimeSpan.Zero),
        SeriesId = series,
        OccurrenceKey = series is null ? null : new DateTimeOffset(2026, 10, day, 19, 0, 0, TimeSpan.Zero)
    };

    private static string EventPath(EventEntity e) => $"/api/events/{e.Id}";
    private static string SeriesPath(Guid series) => $"/api/series/{series}";

    private IRenderedComponent<Events> Loaded()
    {
        var page = harness.Render<Events>();
        page.WaitForRows(4);
        return page;
    }

    private HttpResponseMessage Removed(Predicate<EventEntity> match)
    {
        stored.RemoveAll(match);
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    // Serialized when answered, so a later change to the stored list never alters an answer already sent.
    private static HttpResponseMessage Answer<T>(T body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body, JsonSerializerOptions.Web), Encoding.UTF8, "application/json") };

    // Every request that is not a read, in order.
    private List<string> Writes() =>
        api.Requests.Where(r => r.Method != HttpMethod.Get).Select(r => $"{r.Method.Method} {r.Path}").ToList();

    private static bool HasScopeQuestion(IRenderedComponent<Events> page) => page.FindAll("fluent-dialog.scope-dialog").Count > 0;
    private static IElement ScopeQuestion(IRenderedComponent<Events> page) => page.Find("fluent-dialog.scope-dialog");

    private static List<string> Labels(IRenderedComponent<Events> page) =>
        ScopeQuestion(page).QuerySelectorAll("fluent-radio").Select(r => r.QuerySelector(".scope-label")!.TextContent.Trim()).ToList();

    private static IElement ConfirmButton(IRenderedComponent<Events> page) => ScopeQuestion(page).QuerySelector("fluent-button.scope-confirm")!;

    // Chooses the scope whose visible label is this, as the radio group reports a choice.
    private static Task Choose(IRenderedComponent<Events> page, string label)
    {
        var radio = ScopeQuestion(page).QuerySelectorAll("fluent-radio").Single(r => r.QuerySelector(".scope-label")!.TextContent.Trim() == label);
        return ScopeQuestion(page).QuerySelector("fluent-radio-group")!
            .TriggerEventAsync("onradiogroupchange", new ChangeEventArgs { Value = radio.GetAttribute("current-value") });
    }

    // The click handler of a row's Delete, as the page wired it.
    private static EventCallback<MouseEventArgs> DeleteHandler(IRenderedComponent<Events> page, string row) =>
        page.FindComponents<FluentButton>().Single(b => b.Find("fluent-button").GetAttribute("aria-label") == $"Delete {row}").Instance.OnClick;
}
