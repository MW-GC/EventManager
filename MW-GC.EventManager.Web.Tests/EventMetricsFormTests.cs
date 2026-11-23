using System.Net;
using System.Reflection;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// #135: the Event metrics form in the Event details dialog, rendered with bUnit through the Events page against a
// stub API: reading and saving. Event metrics are read and saved through their own route with the row's ETag
// (ADR 0001), never with the Event, and never shown in the Events list. Validation is in
// EventMetricsValidationTests; stale edits, the busy guard, Discard and the Selections in EventMetricsGuardTests.
[TestClass]
public sealed class EventMetricsFormTests : EventMetricsFormFixture
{
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

        CollectionAssert.AreEqual(new[] { "12", "5", "7", "40.5" }, Values(page));
        Assert.AreEqual(1, api.Count(HttpMethod.Get, metricsPath));
        Assert.IsEmpty(page.FormDialog().Alerts());
    }

    [TestMethod]
    public async Task AnEventWithNoRowShowsEmptyFields()
    {
        var page = await OpenDetails();

        CollectionAssert.AreEqual(new[] { "", "", "", "" }, Values(page));
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

        var fields = Card(page, "Event metrics").QuerySelectorAll("fluent-text-field").ToList();
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

        foreach (var metric in Metrics) await Type(page, metric.Label, "  ");
        await SaveMetrics(page);

        CollectionAssert.AreEqual(new[] { "null", "null", "null", "null" }, MetricsIn(Assert.ContainsSingle(api.Bodies(HttpMethod.Put, metricsPath))));
        Assert.AreEqual(FirstTag, Assert.ContainsSingle(api.IfMatches(HttpMethod.Put, metricsPath)));
        CollectionAssert.AreEqual(new[] { "", "", "", "" }, Values(page));
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
            Selections = [Pick(kart, timeTrial)],
            WinnerActivityId = timeTrial.Id
        };
        var path = $"/api/events/{future.Id}/metrics";
        api.Json(HttpMethod.Get, "/api/events", new[] { future });
        api.On(HttpMethod.Get, path, () => Row(null, null, null, null, null, future.Id));
        api.On(HttpMethod.Put, path, () => Row(30, null, null, 12.25m, FirstTag, future.Id));
        var page = harness.Render<Events>();
        page.WaitForRows(1);
        await RowButton(page, "Next year", "Details").ClickAsync(new());
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
}
