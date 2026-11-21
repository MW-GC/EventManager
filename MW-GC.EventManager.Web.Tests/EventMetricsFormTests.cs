using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
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
// ETag (ADR 0001), never with the Event, and never shown in the Events list. Fields are found by their
// visible label, as a user finds them.
[TestClass]
public sealed class EventMetricsFormTests : IDisposable
{
    // Azure Table Storage tags are weak and carry quotes; they must reach If-Match as read.
    private const string FirstTag = "W/\"datetime'2026-11-20T10%3A00%3A00.0000000Z'\"";
    private const string SecondTag = "W/\"datetime'2026-11-20T11%3A00%3A00.0000000Z'\"";
    private const string StaleMessage = "These numbers were changed by someone else. Reload and try again.";
    private static readonly string[] Labels = ["Attendees", "New attendees", "Returning attendees", "Amount spent"];
    // The JSON names of the four metrics in a request body, in the order of Labels.
    private static readonly string[] MetricNames = ["attendees", "newAttendees", "returningAttendees", "amountSpent"];

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
           // The Winner's Comments panel (#132): none yet, for either Activity.
           .Json(HttpMethod.Get, $"/api/activities/{grandPrix.Id}/comments", Array.Empty<CommentEntity>())
           .Json(HttpMethod.Get, $"/api/activities/{timeTrial.Id}/comments", Array.Empty<CommentEntity>());
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
        Assert.IsNull(FindField(page, "Attendees"));

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

        CollectionAssert.AreEqual(new[] { "12", "5", "7", "40.5" }, MetricsIn(Assert.ContainsSingle(api.Bodies(HttpMethod.Put, metricsPath))));
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

    // #173 items 1 and 7: the client sends the metrics entity itself, so the body carries exactly the entity's
    // JSON properties, and every field the form shows reaches its metric. The expected names are read from the
    // entity type, never from the client, so a hand-listed body (or a metric on the entity with no form field,
    // which would stay null here) fails.
    [TestMethod]
    public async Task TheRequestBodyIsTheEntity_AndEveryMetricInTheListReachesIt()
    {
        var page = await OpenDetails();
        api.On(HttpMethod.Put, metricsPath, () => Row(1, 1, 1, 1m, FirstTag));

        var fields = MetricsCard(page).QuerySelectorAll("fluent-text-field").ToList();
        foreach (var field in fields) await field.InputAsync(new ChangeEventArgs { Value = "1" });
        await SaveMetrics(page);

        using var body = JsonDocument.Parse(Assert.ContainsSingle(api.Bodies(HttpMethod.Put, metricsPath))!);
        var sent = body.RootElement.EnumerateObject().Select(p => p.Name).Order().ToList();
        var entity = typeof(EventMetricsEntity).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).Order().ToList();
        CollectionAssert.AreEqual(entity, sent);

        var metrics = typeof(EventMetricsEntity).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).ToList();
        Assert.HasCount(metrics.Count, fields, "one form field per metric on the entity");
        foreach (var metric in metrics)
            Assert.AreEqual("1", body.RootElement.GetProperty(metric).GetRawText(), metric);
    }

    [TestMethod]
    public async Task ClearingAFieldSendsItAsNull()
    {
        Stored(12, 5, 7, 40.5m, FirstTag);
        var page = await OpenDetails();
        api.On(HttpMethod.Put, metricsPath, () => Row(12, 5, 7, null, SecondTag));

        await Type(page, "Amount spent", "");
        await SaveMetrics(page);

        CollectionAssert.AreEqual(new[] { "12", "5", "7", "null" }, MetricsIn(Assert.ContainsSingle(api.Bodies(HttpMethod.Put, metricsPath))));
        Assert.AreEqual(FirstTag, Assert.ContainsSingle(api.IfMatches(HttpMethod.Put, metricsPath)));
        Assert.AreEqual("", Value(page, "Amount spent") ?? "");
        Assert.AreEqual("12", Value(page, "Attendees"));
    }

    // #173 item 2: an all-null body is valid and clears the values (docs/api-validation.md).
    [TestMethod]
    public async Task ClearingEveryFieldSendsEveryMetricAsNull()
    {
        Stored(12, 5, 7, 40.5m, FirstTag);
        var page = await OpenDetails();
        api.On(HttpMethod.Put, metricsPath, () => Row(null, null, null, null, SecondTag));

        foreach (var label in Labels) await Type(page, label, "  ");
        await SaveMetrics(page);

        CollectionAssert.AreEqual(new[] { "null", "null", "null", "null" }, MetricsIn(Assert.ContainsSingle(api.Bodies(HttpMethod.Put, metricsPath))));
        Assert.AreEqual(FirstTag, Assert.ContainsSingle(api.IfMatches(HttpMethod.Put, metricsPath)));
        CollectionAssert.AreEqual(new[] { "", "", "", "" }, Labels.Select(l => Value(page, l) ?? "").ToList());
        Assert.IsEmpty(page.FormDialog().Alerts());
        Assert.Contains("Event metrics saved.", page.FormDialog().TextContent);
    }

    // #173 item 2: any Event takes metrics, one that has not happened yet too.
    [TestMethod]
    public async Task AFutureEventTakesMetricsToo()
    {
        var future = new EventEntity
        {
            Name = "Next year",
            Date = DateTimeOffset.UtcNow.AddYears(1),
            UniqueGamesOnly = false,
            Selections = [Pick(timeTrial)],
            WinnerActivityId = timeTrial.Id
        };
        var path = $"/api/events/{future.Id}/metrics";
        api.Json(HttpMethod.Get, "/api/events", new[] { future });
        api.On(HttpMethod.Get, path, () => Row(null, null, null, null, null, future.Id));
        api.On(HttpMethod.Put, path, () => Row(30, null, null, 12.25m, FirstTag, future.Id));
        var page = harness.Render<Events>();
        page.WaitForRows(1);
        await DetailsButton(page).ClickAsync(new());
        page.WaitForAssertion(() => Assert.IsNotNull(FindField(page, "Attendees")));

        await Type(page, "Attendees", "30");
        await Type(page, "Amount spent", "12.25");
        await SaveMetrics(page);

        CollectionAssert.AreEqual(new[] { "30", "null", "null", "12.25" }, MetricsIn(Assert.ContainsSingle(api.Bodies(HttpMethod.Put, path))));
        Assert.IsEmpty(page.FormDialog().Alerts());
        Assert.Contains("Event metrics saved.", page.FormDialog().TextContent);
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

        CollectionAssert.AreEqual(new[] { "0", "1000000", "1000000", "1000000000.00" },
            MetricsIn(Assert.ContainsSingle(api.Bodies(HttpMethod.Put, metricsPath))));
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
    // #173 item 2: a comma (decimal or thousands) is refused, never read as another number.
    [DataRow("Amount spent", "40,5", "Amount spent must be between 0 and 1000000000 with at most 2 decimal places.")]
    [DataRow("Attendees", "1,000", "Attendees must be a whole number from 0 to 1000000.")]
    // #173 item 2: past int's range is refused, never wrapped round.
    [DataRow("Attendees", "2147483648", "Attendees must be a whole number from 0 to 1000000.")]
    [DataRow("Returning attendees", "99999999999", "Returning attendees must be a whole number from 0 to 1000000.")]
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

    // #173 item 6: the form's rules are the API's (FieldRules.Count and FieldRules.Money with EventMetricsValidator's
    // limits), value for value, refused and accepted alike. The limits are read as compiled into each assembly, so a
    // change on one side alone fails here.
    [TestMethod]
    public void TheFormsRulesAreTheApis_ValueForValue()
    {
        Assert.AreEqual(1_000_000, typeof(EventMetricsValidator).GetField(nameof(EventMetricsValidator.MaxCount))!.GetRawConstantValue());
        Assert.AreEqual(typeof(EventMetricsValidator).GetField(nameof(EventMetricsValidator.MaxCount))!.GetRawConstantValue(),
            typeof(EventMetricsRules).GetField(nameof(EventMetricsRules.MaxCount))!.GetRawConstantValue());
        // A decimal constant is compiled as a field with its value in a DecimalConstantAttribute.
        Assert.AreEqual(1_000_000_000m, DecimalConstant(typeof(EventMetricsValidator), nameof(EventMetricsValidator.MaxAmount)));
        Assert.AreEqual(DecimalConstant(typeof(EventMetricsValidator), nameof(EventMetricsValidator.MaxAmount)),
            DecimalConstant(typeof(EventMetricsRules), nameof(EventMetricsRules.MaxAmount)));

        foreach (var count in new int?[] { null, int.MinValue, -1, 0, 1, 12, 999_999, 1_000_000, 1_000_001, int.MaxValue })
            Assert.AreEqual(FieldRules.Count(count, "New attendees", EventMetricsValidator.MaxCount),
                EventMetricsRules.Count(count, "New attendees"), $"count {count}");
        foreach (var amount in new decimal?[] { null, -0.01m, 0m, 0.01m, 1.005m, 40.5m, 40.50m, 999_999_999.99m, 1_000_000_000m, 1_000_000_000.01m })
            Assert.AreEqual(FieldRules.Money(amount, "Amount spent", EventMetricsValidator.MaxAmount),
                EventMetricsRules.Money(amount, "Amount spent"), $"amount {amount}");
    }

    // #173 item 9: each message is tied to its field, so a screen reader reads it with the field.
    [TestMethod]
    public async Task EveryFieldMessageIsTiedToItsField()
    {
        var page = await OpenDetails();
        foreach (var label in Labels) Assert.IsNull(Field(page, label).GetAttribute("aria-invalid"), label);
        await Type(page, "Attendees", "-1");
        await Type(page, "New attendees", "x");
        await Type(page, "Returning attendees", "1000001");
        await Type(page, "Amount spent", "1.005");

        await SaveMetrics(page);

        foreach (var label in Labels)
        {
            var field = Field(page, label);
            Assert.AreEqual("true", field.GetAttribute("aria-invalid"), label);
            var described = field.GetAttribute("aria-describedby");
            Assert.IsNotNull(described, label);
            var message = page.FormDialog().QuerySelector($"[id='{described}']");
            Assert.IsNotNull(message, $"{label}: aria-describedby names no element");
            Assert.AreEqual(Assert.ContainsSingle(FieldAlerts(page, label)), message.TextContent.Trim(), label);
        }

        // A fixed field is valid again and names no message.
        await Type(page, "Attendees", "3");
        Assert.IsNull(Field(page, "Attendees").GetAttribute("aria-invalid"));
        Assert.IsNull(Field(page, "Attendees").GetAttribute("aria-describedby"));
        Assert.AreEqual("true", Field(page, "New attendees").GetAttribute("aria-invalid"));
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

    // #173 item 5: after a 409 the tag held is out of date, so Save waits for Reload, even when more is typed;
    // a Reload that fails says so in its own message and leaves Save waiting.
    [TestMethod]
    public async Task AfterAStaleSave_SaveStaysDisabledUntilReload()
    {
        Stored(12, 5, 7, 40.5m, FirstTag);
        var page = await OpenDetails();
        api.Status(HttpMethod.Put, metricsPath, HttpStatusCode.Conflict, "The request conflicts with the stored data. Reload and try again.");
        await Type(page, "Attendees", "20");
        await SaveMetrics(page);
        Assert.AreEqual(StaleMessage, Assert.ContainsSingle(page.FormDialog().Alerts()));

        await Type(page, "Attendees", "21");
        Assert.IsTrue(page.FormDialog().Button("Save metrics").IsBusy(), "a stale tag waits for Reload");
        await page.FormDialog().Button("Save metrics").ClickAsync(new());
        Assert.AreEqual(1, api.Count(HttpMethod.Put, metricsPath));

        api.Status(HttpMethod.Get, metricsPath, HttpStatusCode.InternalServerError);
        await page.FormDialog().Button("Reload").ClickAsync(new());
        page.WaitForAssertion(() => CollectionAssert.AreEqual(
            new[] { StaleMessage, "The Event metrics could not be reloaded. The server had a problem (HTTP 500). Try again." },
            page.FormDialog().Alerts()));
        Assert.AreEqual("21", Value(page, "Attendees"));
        Assert.IsTrue(page.FormDialog().Button("Save metrics").IsBusy(), "still stale after a failed Reload");

        Stored(30, 5, 7, 41m, SecondTag);
        await page.FormDialog().Button("Reload").ClickAsync(new());
        page.WaitForAssertion(() => Assert.AreEqual("30", Value(page, "Attendees")));
        Assert.IsEmpty(page.FormDialog().Alerts());
        await Type(page, "Attendees", "31");
        Assert.IsFalse(page.FormDialog().Button("Save metrics").IsBusy());
        api.On(HttpMethod.Put, metricsPath, () => Row(31, 5, 7, 41m, FirstTag));
        await SaveMetrics(page);
        CollectionAssert.AreEqual(new[] { FirstTag, SecondTag }, api.IfMatches(HttpMethod.Put, metricsPath));
        Assert.Contains("Event metrics saved.", page.FormDialog().TextContent);
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
            // Select Winner goes through Close's busy guard (#186 item 8).
            Assert.IsTrue(page.FormDialog().Button("Select Winner").IsBusy());
        });
        await page.FormDialog().Button("Save metrics").ClickAsync(new());
        await page.FormDialog().Button("Select Winner").ClickAsync(new());
        Assert.AreEqual(0, api.Count(HttpMethod.Put, eventPath));
        // Overlay click or Escape while it saves does nothing.
        await Dismiss(page);
        Assert.IsTrue(page.HasFormDialog());
        Assert.IsFalse(page.HasConfirmation());

        gate.SetResult();
        await first;

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

    // #173 item 4: the change check compares values, not text, so the stored amount written with a trailing
    // zero, or a count with spaces round it, is no change: Save stays off and Escape closes without asking.
    [TestMethod]
    public async Task ATrailingZeroIsNoChange()
    {
        Stored(12, null, null, 40.5m, FirstTag);
        var page = await OpenDetails();

        await Type(page, "Amount spent", "40.50");
        await Type(page, "Attendees", " 12 ");

        Assert.IsTrue(page.FormDialog().Button("Save metrics").IsBusy(), "40.50 is the stored 40.5");
        await Dismiss(page);
        Assert.IsFalse(page.HasConfirmation());
        Assert.IsFalse(page.HasFormDialog());

        // A different value is a change.
        await DetailsButton(page).ClickAsync(new());
        page.WaitForAssertion(() => Assert.AreEqual("40.5", Value(page, "Amount spent")));
        await Type(page, "Amount spent", "40.51");
        Assert.IsFalse(page.FormDialog().Button("Save metrics").IsBusy());
        await Dismiss(page);
        Assert.IsTrue(page.HasConfirmation());
    }

    // ---- Selections stay apart -----------------------------------------------------------------

    // The stub stores what the form saved and answers every read with it, as the API does. Changing the Winner
    // (an Event PUT) sends exactly the Event's own properties, and the metrics read back afterwards are the saved ones.
    [TestMethod]
    public async Task SavingSelectionsAfterMetricsLeavesTheMetricsIntact()
    {
        string? storedBody = null;
        api.On(HttpMethod.Get, metricsPath, () => StoredRow(storedBody));
        api.On(HttpMethod.Put, metricsPath, () =>
        {
            storedBody = api.Bodies(HttpMethod.Put, metricsPath).Last();
            return StoredRow(storedBody);
        });
        var page = await OpenDetails();
        await Type(page, "Attendees", "12");
        await Type(page, "New attendees", "5");
        await Type(page, "Returning attendees", "7");
        await Type(page, "Amount spent", "40.5");
        await SaveMetrics(page);

        var rewon = new EventEntity { Id = past.Id, Name = past.Name, Date = past.Date, UniqueGamesOnly = false, Selections = past.Selections, WinnerActivityId = timeTrial.Id };
        api.Json(HttpMethod.Put, eventPath, rewon)
           .Json(HttpMethod.Get, "/api/events", new[] { rewon });
        await page.FormDialog().Button("Select Winner").ClickAsync(new());

        // The Event PUT carries the Event's properties and nothing else.
        using (var eventBody = JsonDocument.Parse(Assert.ContainsSingle(api.Bodies(HttpMethod.Put, eventPath))!))
        {
            CollectionAssert.AreEqual(
                new[] { "date", "eTag", "id", "name", "partitionKey", "rowKey", "selections", "timestamp", "uniqueGamesOnly", "winnerActivityId" },
                eventBody.RootElement.EnumerateObject().Select(p => p.Name).Order().ToList());
            Assert.AreEqual(timeTrial.Id, eventBody.RootElement.GetProperty("winnerActivityId").GetGuid());
        }
        Assert.AreEqual(1, api.Count(HttpMethod.Put, metricsPath));
        CollectionAssert.AreEqual(new[] { "12", "5", "7", "40.5" }, Labels.Select(l => Value(page, l)).ToList());

        // Read again from the store after the Event PUT: the saved metrics, untouched.
        await page.FormDialog().Button("Close").ClickAsync(new());
        await DetailsButton(page).ClickAsync(new());
        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { "12", "5", "7", "40.5" }, Labels.Select(l => Value(page, l)).ToList()));
        Assert.AreEqual(2, api.Count(HttpMethod.Get, metricsPath));
    }

    // ---- Helpers -------------------------------------------------------------------------------

    // A decimal constant as compiled into its own assembly.
    private static decimal DecimalConstant(Type type, string name) =>
        type.GetField(name)!.GetCustomAttribute<System.Runtime.CompilerServices.DecimalConstantAttribute>()!.Value;

    // The API's answer for a metrics row: the body (eTag included, as the API sends it) and the ETag header.
    private HttpResponseMessage Row(int? attendees, int? newAttendees, int? returning, decimal? amount, string? eTag, Guid? eventId = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { id = eventId ?? past.Id, attendees, newAttendees, returningAttendees = returning, amountSpent = amount, eTag })
        };
        if (eTag is not null) response.Headers.TryAddWithoutValidation("ETag", eTag);
        return response;
    }

    // The row as stored from a saved request body, or no row yet.
    private HttpResponseMessage StoredRow(string? body)
    {
        if (body is null) return Row(null, null, null, null, null);
        using var json = JsonDocument.Parse(body);
        int? Count(string name) => json.RootElement.GetProperty(name) is { ValueKind: JsonValueKind.Number } n ? n.GetInt32() : null;
        var amount = json.RootElement.GetProperty("amountSpent") is { ValueKind: JsonValueKind.Number } a ? a.GetDecimal() : (decimal?)null;
        return Row(Count("attendees"), Count("newAttendees"), Count("returningAttendees"), amount, SecondTag);
    }

    private void Stored(int? attendees, int? newAttendees, int? returning, decimal? amount, string? eTag) =>
        api.On(HttpMethod.Get, metricsPath, () => Row(attendees, newAttendees, returning, amount, eTag));

    // The four metrics in a request body, as written (null for a cleared one), in the order of Labels.
    private static List<string> MetricsIn(string? body)
    {
        using var json = JsonDocument.Parse(body!);
        return MetricNames.Select(n => json.RootElement.GetProperty(n).GetRawText()).ToList();
    }

    private async Task<IRenderedComponent<Events>> OpenDetails()
    {
        var page = harness.Render<Events>();
        page.WaitForRows(1);
        await DetailsButton(page).ClickAsync(new());
        page.WaitForAssertion(() => Assert.IsNotNull(FindField(page, "Attendees")));
        return page;
    }

    private static IElement DetailsButton(IRenderedComponent<Events> page) =>
        page.FindAll("tbody tr.fluent-data-grid-row").Single(r => r.Closest("fluent-dialog") is null)
            .QuerySelector("fluent-button[title=Details]")!;

    // The card of the Event metrics form in the details dialog.
    private static IElement MetricsCard(IRenderedComponent<Events> page) =>
        page.FormDialog().QuerySelectorAll("fluent-card")
            .Single(c => c.QuerySelectorAll(".fluent-typography").Any(l => l.TextContent.Trim() == "Event metrics"));

    // The text field a visible label names (the label's "for"), or null when the form shows no such field.
    private static IElement? FindField(IRenderedComponent<Events> page, string label)
    {
        var dialog = page.FormDialog();
        var target = dialog.QuerySelectorAll("label").FirstOrDefault(l => l.TextContent.Trim() == label)?.GetAttribute("for");
        return target is null ? null : dialog.QuerySelector($"[id='{target}']");
    }

    private static IElement Field(IRenderedComponent<Events> page, string label) =>
        FindField(page, label) ?? throw new AssertFailedException($"No field labelled {label}.");

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
}
