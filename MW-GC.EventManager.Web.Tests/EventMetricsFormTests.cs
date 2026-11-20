using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Pages;
using MW_GC.EventManager.Web.Shared.Events;

namespace MW_GC.EventManager.Web.Tests;

// #135: the Event metrics form in the Event details dialog, rendered with bUnit through the Events
// page against a stub API. Event metrics are read and saved through their own route with the row's
// ETag (ADR 0001), never with the Event, and never shown in the Events list.
[TestClass]
public sealed class EventMetricsFormTests : IDisposable
{
    // Azure Table Storage tags are weak and carry quotes; they must reach If-Match as read.
    private const string FirstTag = "W/\"datetime'2026-11-20T10%3A00%3A00.0000000Z'\"";
    private const string SecondTag = "W/\"datetime'2026-11-20T11%3A00%3A00.0000000Z'\"";
    private const string StaleMessage = "These numbers were changed by someone else. Reload and try again.";
    private static readonly string[] Labels = ["Attendees", "New attendees", "Returning attendees", "Amount spent"];

    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly GameEntity kart = new() { Name = "Kart" };
    private readonly ActivityEntity grandPrix;
    private readonly ActivityEntity timeTrial;
    private readonly EventEntity past;
    private readonly string metricsPath;
    private readonly string eventPath;

    public EventMetricsFormTests()
    {
        grandPrix = new ActivityEntity { Name = "Grand Prix", GameId = kart.Id };
        timeTrial = new ActivityEntity { Name = "Time Trial", GameId = kart.Id };
        // A past Event: metrics are recorded after the night was played.
        past = new EventEntity
        {
            Name = "Past night",
            Date = new DateTimeOffset(2025, 3, 14, 19, 0, 0, TimeSpan.Zero),
            UniqueGamesOnly = false,
            Selections = [Pick(grandPrix), Pick(timeTrial)],
            WinnerActivityId = grandPrix.Id
        };
        metricsPath = $"/api/events/{past.Id}/metrics";
        eventPath = $"/api/events/{past.Id}";
        api.Json(HttpMethod.Get, "/api/games", new[] { kart })
           .Json(HttpMethod.Get, "/api/activities", new[] { grandPrix, timeTrial })
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>())
           .Json(HttpMethod.Get, "/api/events", new[] { past })
           // The Winner's Comments panel (#132): none yet.
           .Json(HttpMethod.Get, $"/api/activities/{grandPrix.Id}/comments", Array.Empty<CommentEntity>());
        // No row yet: every field null and no ETag header, as the API answers.
        Stored(null, null, null, null, null);
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    private Selection Pick(ActivityEntity act) => new()
    {
        Game = new Game { Id = kart.Id, Name = kart.Name },
        Activity = new Activity { Id = act.Id, GameId = act.GameId, Name = act.Name }
    };

    // ---- The list ------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheEventsListNeitherReadsNorShowsMetrics()
    {
        Stored(9876, 5432, 4444, 543.21m, FirstTag);
        var page = harness.Render<Events>();
        page.WaitForRows(1);

        var row = Assert.ContainsSingle(page.Rows());
        Assert.AreEqual(0, api.Count(HttpMethod.Get, metricsPath));
        foreach (var text in new[] { "9876", "5432", "4444", "543.21", "Attendees", "Amount spent" })
            Assert.DoesNotContain(text, row);

        // Only the details dialog reads them; closing it leaves the list as it was.
        await DetailsButton(page).ClickAsync(new());
        page.WaitForAssertion(() => Assert.AreEqual("9876", Value(page, "Attendees")));
        await page.FormDialog().Button("Close").ClickAsync(new());
        Assert.AreEqual(row, Assert.ContainsSingle(page.Rows()));
    }

    // ---- Reading -------------------------------------------------------------------------------

    [TestMethod]
    public async Task DetailsShowTheStoredMetricsOfAPastEvent()
    {
        Stored(12, 5, 7, 40.5m, FirstTag);

        var page = await OpenDetails();

        CollectionAssert.AreEqual(new[] { "12", "5", "7", "40.5" }, Labels.Select(l => Value(page, l)).ToList());
        Assert.AreEqual(1, api.Count(HttpMethod.Get, metricsPath));
        Assert.IsEmpty(page.FormDialog().Alerts());
    }

    [TestMethod]
    public async Task AnEventWithNoRowShowsEmptyFields()
    {
        var page = await OpenDetails();

        CollectionAssert.AreEqual(new[] { "", "", "", "" }, Labels.Select(l => Value(page, l) ?? "").ToList());
        Assert.IsTrue(page.FormDialog().Button("Save metrics").IsBusy(), "nothing to save yet");
    }

    [TestMethod]
    public async Task AFailedReadSaysSo_AndRetryLoadsTheForm()
    {
        api.Status(HttpMethod.Get, metricsPath, HttpStatusCode.InternalServerError);
        var page = harness.Render<Events>();
        page.WaitForRows(1);
        await DetailsButton(page).ClickAsync(new());

        page.WaitForAssertion(() => Assert.Contains(
            "Event metrics could not be loaded. The server had a problem (HTTP 500). Try again.", page.FormDialog().TextContent));
        Assert.IsNull(page.FormDialog().QuerySelector("#metrics-attendees"));

        Stored(3, null, null, null, FirstTag);
        await page.FormDialog().Button("Retry").ClickAsync(new());

        page.WaitForAssertion(() => Assert.AreEqual("3", Value(page, "Attendees")));
        Assert.DoesNotContain("Event metrics could not be loaded", page.FormDialog().TextContent);
    }

    // ---- Saving --------------------------------------------------------------------------------

    [TestMethod]
    public async Task FirstSaveSendsNoIfMatch_ALaterSaveSendsTheTagItGotBack()
    {
        var page = await OpenDetails();
        api.On(HttpMethod.Put, metricsPath, () => Row(12, 5, 7, 40.5m, FirstTag));

        await Type(page, "Attendees", "12");
        await Type(page, "New attendees", "5");
        await Type(page, "Returning attendees", "7");
        await Type(page, "Amount spent", "40.5");
        await SaveMetrics(page);

        Assert.AreEqual("{\"attendees\":12,\"newAttendees\":5,\"returningAttendees\":7,\"amountSpent\":40.5}",
            Assert.ContainsSingle(api.Bodies(HttpMethod.Put, metricsPath)));
        Assert.IsNull(Assert.ContainsSingle(api.IfMatches(HttpMethod.Put, metricsPath)));
        Assert.Contains("Event metrics saved.", page.FormDialog().TextContent);
        Assert.IsEmpty(page.FormDialog().Alerts());

        api.On(HttpMethod.Put, metricsPath, () => Row(13, 5, 7, 40.5m, SecondTag));
        await Type(page, "Attendees", "13");
        await SaveMetrics(page);
        await Type(page, "Attendees", "14");
        await SaveMetrics(page);

        CollectionAssert.AreEqual(new[] { null, FirstTag, SecondTag }, api.IfMatches(HttpMethod.Put, metricsPath));
        // The Event's own route is never written.
        Assert.AreEqual(0, api.Count(HttpMethod.Put, eventPath));
    }

    [TestMethod]
    public async Task ClearingAFieldSendsItAsNull()
    {
        Stored(12, 5, 7, 40.5m, FirstTag);
        var page = await OpenDetails();
        api.On(HttpMethod.Put, metricsPath, () => Row(12, 5, 7, null, SecondTag));

        await Type(page, "Amount spent", "");
        await SaveMetrics(page);

        Assert.AreEqual("{\"attendees\":12,\"newAttendees\":5,\"returningAttendees\":7,\"amountSpent\":null}",
            Assert.ContainsSingle(api.Bodies(HttpMethod.Put, metricsPath)));
        Assert.AreEqual(FirstTag, Assert.ContainsSingle(api.IfMatches(HttpMethod.Put, metricsPath)));
        Assert.AreEqual("", Value(page, "Amount spent") ?? "");
        Assert.AreEqual("12", Value(page, "Attendees"));
    }

    [TestMethod]
    public async Task TheLimitsThemselvesAreAccepted()
    {
        var page = await OpenDetails();
        api.On(HttpMethod.Put, metricsPath, () => Row(0, 1_000_000, 1_000_000, 1_000_000_000m, FirstTag));

        await Type(page, "Attendees", "0");
        await Type(page, "New attendees", "1000000");
        await Type(page, "Returning attendees", " 1000000 ");
        await Type(page, "Amount spent", "1000000000.00");
        await SaveMetrics(page);

        Assert.AreEqual("{\"attendees\":0,\"newAttendees\":1000000,\"returningAttendees\":1000000,\"amountSpent\":1000000000.00}",
            Assert.ContainsSingle(api.Bodies(HttpMethod.Put, metricsPath)));
        Assert.IsEmpty(page.FormDialog().Alerts());
    }

    [TestMethod]
    public async Task AFailedSaveKeepsTheTypedValuesAndSaysWhy()
    {
        var page = await OpenDetails();
        api.Status(HttpMethod.Put, metricsPath, HttpStatusCode.InternalServerError);

        await Type(page, "Attendees", "12");
        await SaveMetrics(page);

        Assert.AreEqual("The Event metrics were not saved. The server had a problem (HTTP 500). Try again.",
            Assert.ContainsSingle(page.FormDialog().Alerts()));
        Assert.AreEqual("12", Value(page, "Attendees"));
        Assert.IsFalse(page.FormDialog().Button("Save metrics").IsBusy(), "still unsaved, so Save stays usable");
    }

    // ---- Validation ----------------------------------------------------------------------------

    [TestMethod]
    [DataRow("Attendees", "-1", "Attendees must be a whole number from 0 to 1000000.")]
    [DataRow("Attendees", "2.5", "Attendees must be a whole number from 0 to 1000000.")]
    [DataRow("New attendees", "abc", "New attendees must be a whole number from 0 to 1000000.")]
    [DataRow("New attendees", "1000001", "New attendees must be a whole number from 0 to 1000000.")]
    [DataRow("Returning attendees", "-3", "Returning attendees must be a whole number from 0 to 1000000.")]
    [DataRow("Amount spent", "-0.01", "Amount spent must be between 0 and 1000000000 with at most 2 decimal places.")]
    [DataRow("Amount spent", "1.005", "Amount spent must be between 0 and 1000000000 with at most 2 decimal places.")]
    [DataRow("Amount spent", "1000000000.01", "Amount spent must be between 0 and 1000000000 with at most 2 decimal places.")]
    [DataRow("Amount spent", "ten", "Amount spent must be between 0 and 1000000000 with at most 2 decimal places.")]
    public async Task AnInvalidValueShowsItsMessageUnderTheField_AndSendsNothing(string label, string typed, string message)
    {
        var page = await OpenDetails();

        await Type(page, label, typed);
        await SaveMetrics(page);

        Assert.AreEqual(message, Assert.ContainsSingle(FieldAlerts(page, label)));
        Assert.AreEqual(message, Assert.ContainsSingle(page.FormDialog().Alerts()));
        Assert.AreEqual(0, api.Count(HttpMethod.Put, metricsPath));
        Assert.AreEqual(typed, Value(page, label));
    }

    // The form's messages are the API's own words: each is compared with EventMetricsValidator.
    [TestMethod]
    public async Task EveryFieldsMessageIsTheApis()
    {
        var page = await OpenDetails();
        var cases = new (string Label, string Typed, EventMetricsEntity Same)[]
        {
            ("Attendees", "-1", new() { Attendees = -1 }),
            ("New attendees", "1000001", new() { NewAttendees = 1_000_001 }),
            ("Returning attendees", "-5", new() { ReturningAttendees = -5 }),
            ("Amount spent", "1.005", new() { AmountSpent = 1.005m }),
        };
        foreach (var c in cases) await Type(page, c.Label, c.Typed);

        await SaveMetrics(page);

        foreach (var c in cases)
            Assert.AreEqual(EventMetricsValidator.Validate(c.Same), Assert.ContainsSingle(FieldAlerts(page, c.Label)), c.Label);
        Assert.AreEqual(0, api.Count(HttpMethod.Put, metricsPath));
    }

    [TestMethod]
    public async Task FixingTheValueClearsItsMessage_AndTheSaveGoesThrough()
    {
        var page = await OpenDetails();
        await Type(page, "Attendees", "-1");
        await SaveMetrics(page);
        Assert.ContainsSingle(FieldAlerts(page, "Attendees"));

        api.On(HttpMethod.Put, metricsPath, () => Row(1, null, null, null, FirstTag));
        await Type(page, "Attendees", "1");
        Assert.IsEmpty(FieldAlerts(page, "Attendees"));
        await SaveMetrics(page);

        Assert.AreEqual(1, api.Count(HttpMethod.Put, metricsPath));
        Assert.IsEmpty(page.FormDialog().Alerts());
    }

    // ---- Stale edit ----------------------------------------------------------------------------

    [TestMethod]
    public async Task AStaleSaveShowsTheMessageKeepsTheTypedValues_AndReloadReadsTheRowAgain()
    {
        Stored(12, 5, 7, 40.5m, FirstTag);
        var page = await OpenDetails();
        api.Status(HttpMethod.Put, metricsPath, HttpStatusCode.Conflict, "The request conflicts with the stored data. Reload and try again.");

        await Type(page, "Attendees", "20");
        await SaveMetrics(page);

        Assert.AreEqual(StaleMessage, Assert.ContainsSingle(page.FormDialog().Alerts()));
        Assert.AreEqual(FirstTag, Assert.ContainsSingle(api.IfMatches(HttpMethod.Put, metricsPath)));
        // Not wiped: the typed value stays until the user chooses Reload.
        Assert.AreEqual("20", Value(page, "Attendees"));
        Assert.AreEqual(1, api.Count(HttpMethod.Get, metricsPath));

        Stored(30, 5, 7, 41m, SecondTag);
        await page.FormDialog().Button("Reload").ClickAsync(new());

        page.WaitForAssertion(() => Assert.AreEqual("30", Value(page, "Attendees")));
        Assert.AreEqual("41", Value(page, "Amount spent"));
        Assert.AreEqual(2, api.Count(HttpMethod.Get, metricsPath));
        Assert.IsEmpty(page.FormDialog().Alerts());

        // The next save sends the tag that Reload read.
        api.On(HttpMethod.Put, metricsPath, () => Row(31, 5, 7, 41m, FirstTag));
        await Type(page, "Attendees", "31");
        await SaveMetrics(page);
        CollectionAssert.AreEqual(new[] { FirstTag, SecondTag }, api.IfMatches(HttpMethod.Put, metricsPath));
        Assert.IsEmpty(page.FormDialog().Alerts());
    }

    [TestMethod]
    public async Task AFirstSaveThatLosesTheRaceIsAStaleEditToo()
    {
        var page = await OpenDetails();
        api.Status(HttpMethod.Put, metricsPath, HttpStatusCode.Conflict, "The request conflicts with the stored data. Reload and try again.");

        await Type(page, "Attendees", "12");
        await SaveMetrics(page);

        Assert.IsNull(Assert.ContainsSingle(api.IfMatches(HttpMethod.Put, metricsPath)));
        Assert.AreEqual(StaleMessage, Assert.ContainsSingle(page.FormDialog().Alerts()));
        Assert.AreEqual("12", Value(page, "Attendees"));
    }

    // ---- Busy guard ----------------------------------------------------------------------------

    [TestMethod]
    public async Task ASecondSaveWhileOneIsInFlightSendsNothing()
    {
        var page = await OpenDetails();
        var gate = api.Hold(HttpMethod.Put, metricsPath, () => Row(12, null, null, null, FirstTag));
        await Type(page, "Attendees", "12");

        var first = page.FormDialog().Button("Save metrics").ClickAsync(new());

        // The click may still be queued behind other renderer work, so wait for its render.
        page.WaitForAssertion(() =>
        {
            Assert.IsTrue(page.FormDialog().Button("Save metrics").IsBusy());
            Assert.IsTrue(page.FormDialog().Button("Close").IsBusy());
        });
        await page.FormDialog().Button("Save metrics").ClickAsync(new());
        // A click that lands before the re-render reaches the handler itself, which sends nothing.
        // Not awaited before the release: without the guard it would wait on the held request too.
        var second = page.InvokeAsync(() => (Task)Call(page.FindComponent<EventMetricsForm>().Instance, "Save")!);
        // Overlay click or Escape while it saves does nothing.
        await Dismiss(page);
        Assert.IsTrue(page.HasFormDialog());
        Assert.IsFalse(page.HasConfirmation());

        gate.SetResult();
        await first;
        await second;

        Assert.AreEqual(1, api.Count(HttpMethod.Put, metricsPath));
        page.WaitForAssertion(() => Assert.IsFalse(page.FormDialog().Button("Close").IsBusy()));
        Assert.Contains("Event metrics saved.", page.FormDialog().TextContent);
    }

    // ---- Discard -------------------------------------------------------------------------------

    [TestMethod]
    public async Task UnsavedMetricsJoinTheDialogsDiscardQuestion()
    {
        Stored(12, null, null, null, FirstTag);
        var page = await OpenDetails();

        // Typed, cursor still in the field: no change event yet, and it already counts.
        await Field(page, "Returning attendees").InputAsync(new ChangeEventArgs { Value = "7" });
        await Dismiss(page);

        Assert.AreEqual("Discard changes?", page.Confirmation().QuerySelector("h4")!.TextContent.Trim());
        await page.Confirmation().Button("Keep editing").ClickAsync(new());
        Assert.IsTrue(page.HasFormDialog());
        Assert.AreEqual("7", Value(page, "Returning attendees"));

        await Dismiss(page);
        await page.ConfirmAsync("Discard");

        Assert.IsFalse(page.HasFormDialog());
        Assert.IsEmpty(api.Requests.Where(r => r.Method != HttpMethod.Get).ToList());
    }

    [TestMethod]
    public async Task TypingBackTheStoredValueOrSavingLeavesNothingToDiscard()
    {
        Stored(12, null, null, null, FirstTag);
        var page = await OpenDetails();

        await Type(page, "Attendees", "13");
        await Type(page, "Attendees", "12");
        await Dismiss(page);
        Assert.IsFalse(page.HasConfirmation());
        Assert.IsFalse(page.HasFormDialog());

        await DetailsButton(page).ClickAsync(new());
        page.WaitForAssertion(() => Assert.AreEqual("12", Value(page, "Attendees")));
        api.On(HttpMethod.Put, metricsPath, () => Row(13, null, null, null, SecondTag));
        await Type(page, "Attendees", "13");
        await SaveMetrics(page);
        await Dismiss(page);

        Assert.IsFalse(page.HasConfirmation());
        Assert.IsFalse(page.HasFormDialog());
    }

    // ---- Selections stay apart -----------------------------------------------------------------

    [TestMethod]
    public async Task SavingSelectionsAfterMetricsLeavesTheMetricsIntact()
    {
        var page = await OpenDetails();
        api.On(HttpMethod.Put, metricsPath, () => Row(12, 5, 7, 40.5m, FirstTag));
        await Type(page, "Attendees", "12");
        await Type(page, "New attendees", "5");
        await Type(page, "Returning attendees", "7");
        await Type(page, "Amount spent", "40.5");
        await SaveMetrics(page);

        var rewon = new EventEntity { Id = past.Id, Name = past.Name, Date = past.Date, UniqueGamesOnly = false, Selections = past.Selections, WinnerActivityId = timeTrial.Id };
        api.Json(HttpMethod.Put, eventPath, rewon)
           .Json(HttpMethod.Get, "/api/events", new[] { rewon });
        await page.FormDialog().Button("Select Winner").ClickAsync(new());

        var eventBody = Assert.ContainsSingle(api.Bodies(HttpMethod.Put, eventPath))!;
        Assert.Contains(timeTrial.Id.ToString(), eventBody);
        foreach (var metric in new[] { "attendees", "amountSpent", "metrics" })
            Assert.DoesNotContain(metric, eventBody, StringComparison.OrdinalIgnoreCase);
        // One metrics write, no re-read, and the form still shows what was saved.
        Assert.AreEqual(1, api.Count(HttpMethod.Put, metricsPath));
        Assert.AreEqual(1, api.Count(HttpMethod.Get, metricsPath));
        CollectionAssert.AreEqual(new[] { "12", "5", "7", "40.5" }, Labels.Select(l => Value(page, l)).ToList());
    }

    // ---- Helpers -------------------------------------------------------------------------------

    // The API's answer for a metrics row: the body (eTag included, as the API sends it) and the ETag header.
    private HttpResponseMessage Row(int? attendees, int? newAttendees, int? returning, decimal? amount, string? eTag)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { id = past.Id, attendees, newAttendees, returningAttendees = returning, amountSpent = amount, eTag })
        };
        if (eTag is not null) response.Headers.TryAddWithoutValidation("ETag", eTag);
        return response;
    }

    private void Stored(int? attendees, int? newAttendees, int? returning, decimal? amount, string? eTag) =>
        api.On(HttpMethod.Get, metricsPath, () => Row(attendees, newAttendees, returning, amount, eTag));

    private async Task<IRenderedComponent<Events>> OpenDetails()
    {
        var page = harness.Render<Events>();
        page.WaitForRows(1);
        await DetailsButton(page).ClickAsync(new());
        page.WaitForAssertion(() => Assert.IsNotNull(page.FormDialog().QuerySelector("#metrics-attendees")));
        return page;
    }

    private static IElement DetailsButton(IRenderedComponent<Events> page) =>
        page.FindAll("tbody tr.fluent-data-grid-row").Single(r => r.Closest("fluent-dialog") is null)
            .QuerySelector("fluent-button[title=Details]")!;

    private static string Id(string label) => "metrics-" + label.ToLowerInvariant().Replace(' ', '-');

    private static IElement Field(IRenderedComponent<Events> page, string label) =>
        page.FormDialog().QuerySelector("#" + Id(label))!;

    private static string? Value(IRenderedComponent<Events> page, string label) => Field(page, label).GetAttribute("value");

    private static Task Type(IRenderedComponent<Events> page, string label, string text) =>
        Field(page, label).InputAsync(new ChangeEventArgs { Value = text });

    // The messages shown right under one field.
    private static List<string> FieldAlerts(IRenderedComponent<Events> page, string label) =>
        Field(page, label).ParentElement!.Alerts();

    private static async Task SaveMetrics(IRenderedComponent<Events> page)
    {
        await page.FormDialog().Button("Save metrics").ClickAsync(new());
        page.WaitForAssertion(() => Assert.IsFalse(page.FormDialog().Button("Close").IsBusy()));
    }

    // Overlay click or Escape on the details dialog.
    private static Task Dismiss(IRenderedComponent<Events> page)
    {
        var dialog = page.FormDialog();
        return dialog.TriggerEventAsync("ondialogdismiss", new DialogEventArgs { Id = dialog.Id, Reason = "dismiss" });
    }

    private static object? Call(object component, string name, params object[] args) =>
        component.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, args);
}
