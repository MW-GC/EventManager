using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Bunit;
using Microsoft.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// #135: the Event metrics form's guards: a stale save waits for Reload, one save runs at a time, unsaved values
// join the dialog's Discard question, and saving the Selections never touches the stored metrics.
[TestClass]
public sealed class EventMetricsGuardTests : EventMetricsFormFixture
{
    private const string StaleMessage = "These numbers were changed by someone else. Reload and try again.";
    private const string ConflictBody = "The request conflicts with the stored data. Reload and try again.";

    // ---- Stale edit ----------------------------------------------------------------------------

    [TestMethod]
    public async Task AStaleSaveShowsTheMessageKeepsTheTypedValues_AndReloadReadsTheRowAgain()
    {
        Stored(12, 5, 7, 40.5m, FirstTag);
        var page = await OpenDetails();
        api.Status(HttpMethod.Put, metricsPath, HttpStatusCode.Conflict, ConflictBody);

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
        api.Status(HttpMethod.Put, metricsPath, HttpStatusCode.Conflict, ConflictBody);
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
        api.Status(HttpMethod.Put, metricsPath, HttpStatusCode.Conflict, ConflictBody);

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
        // Save's handler, as the form wired it, taken while the button is enabled.
        var save = ClickHandler(page, "Save metrics");

        var first = page.FormDialog().Button("Save metrics").ClickAsync(new());

        // The click may still be queued behind other renderer work, so wait for its render.
        page.WaitForAssertion(() =>
        {
            Assert.IsTrue(page.FormDialog().Button("Save metrics").IsBusy());
            Assert.IsTrue(page.FormDialog().Button("Close").IsBusy());
            // Select Winner goes through Close's busy guard (#186 item 8).
            Assert.IsTrue(page.FormDialog().Button("Select Winner").IsBusy());
        });
        await page.FormDialog().Button("Select Winner").ClickAsync(new());
        Assert.AreEqual(0, api.Count(HttpMethod.Put, eventPath));
        // A click that lands before the re-render reaches Save itself, past the disabled button: Save's own guard
        // sends nothing. Not awaited before the release: without the guard it would wait on the held request too.
        var second = page.InvokeAsync(() => save.InvokeAsync(new()));
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

    // The stub API stores the metrics and answers every metrics read with what it stores. Its Event PUT writes any
    // metric the Event body carries into that store, as a store that kept both in one row would (null included).
    // So a Winner save that carried the metrics, or that sent a metrics write of its own, changes what the form
    // reads back afterwards.
    [TestMethod]
    public async Task SavingSelectionsAfterMetricsLeavesTheMetricsIntact()
    {
        var store = new JsonObject();
        api.On(HttpMethod.Get, metricsPath, () => StoreRow(store))
           .On(HttpMethod.Put, metricsPath, () =>
           {
               var sent = JsonNode.Parse(api.Bodies(HttpMethod.Put, metricsPath).Last()!)!;
               foreach (var metric in Metrics) store[metric.JsonName] = sent[metric.JsonName]?.DeepClone();
               return StoreRow(store);
           });
        var page = await OpenDetails();
        await Type(page, "Attendees", "12");
        await Type(page, "New attendees", "5");
        await Type(page, "Returning attendees", "7");
        await Type(page, "Amount spent", "40.5");
        await SaveMetrics(page);

        var rewon = new EventEntity { Id = past.Id, Name = past.Name, Date = past.Date, UniqueGamesOnly = false, Selections = past.Selections, WinnerActivityId = timeTrial.Id };
        api.Json(HttpMethod.Get, "/api/events", new[] { rewon })
           .On(HttpMethod.Put, eventPath, () =>
           {
               var sent = JsonNode.Parse(api.Bodies(HttpMethod.Put, eventPath).Last()!)!.AsObject();
               foreach (var metric in Metrics)
                   if (sent.TryGetPropertyValue(metric.JsonName, out var value)) store[metric.JsonName] = value?.DeepClone();
               return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sent.ToJsonString(), Encoding.UTF8, "application/json") };
           });
        await page.FormDialog().Button("Select Winner").ClickAsync(new());

        page.WaitForAssertion(() => Assert.AreEqual(1, api.Count(HttpMethod.Put, eventPath)));
        Assert.Contains($"\"winnerActivityId\":\"{timeTrial.Id}\"", Assert.ContainsSingle(api.Bodies(HttpMethod.Put, eventPath))!);
        Assert.AreEqual(1, api.Count(HttpMethod.Put, metricsPath));
        CollectionAssert.AreEqual(new[] { "12", "5", "7", "40.5" }, Values(page));

        // Read again from the store after the Event PUT: the saved metrics, untouched.
        await page.FormDialog().Button("Close").ClickAsync(new());
        await DetailsButton(page).ClickAsync(new());
        page.WaitForAssertion(() => Assert.AreEqual(2, api.Count(HttpMethod.Get, metricsPath)));
        page.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { "12", "5", "7", "40.5" }, Values(page)));
    }

    // The stub's stored metrics row as the API answers it, with its ETag (none before the first save).
    private HttpResponseMessage StoreRow(JsonObject store)
    {
        var tag = store.Count == 0 ? null : SecondTag;
        var body = new JsonObject { ["id"] = past.Id, ["eTag"] = tag };
        foreach (var metric in Metrics) body[metric.JsonName] = store[metric.JsonName]?.DeepClone();
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        if (tag is not null) response.Headers.TryAddWithoutValidation("ETag", tag);
        return response;
    }
}
