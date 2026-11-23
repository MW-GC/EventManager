using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// The Events page with one past Event, rendered with bUnit against a stub API, for the tests of its Event metrics
// form (#135): EventMetricsFormTests, EventMetricsValidationTests and EventMetricsGuardTests. Fields are found
// by their visible label, as a user finds them.
public abstract class EventMetricsFormFixture : IDisposable
{
    // Azure Table Storage tags are weak and carry quotes; they must reach If-Match as read.
    protected const string FirstTag = "W/\"datetime'2026-11-20T10%3A00%3A00.0000000Z'\"";
    protected const string SecondTag = "W/\"datetime'2026-11-20T11%3A00%3A00.0000000Z'\"";

    // The four metrics in form order: each field's visible label and its JSON name in a request body.
    protected sealed record Metric(string Label, string JsonName);

    protected static readonly Metric[] Metrics =
    [
        new("Attendees", "attendees"),
        new("New attendees", "newAttendees"),
        new("Returning attendees", "returningAttendees"),
        new("Amount spent", "amountSpent"),
    ];

    internal readonly StubApiHandler api = new();
    internal readonly PageHarness harness;
    protected readonly GameEntity kart = new() { Name = "Kart" };
    protected readonly ActivityEntity grandPrix;
    protected readonly ActivityEntity timeTrial;
    protected readonly EventEntity past;
    protected readonly string metricsPath;
    protected readonly string eventPath;

    protected EventMetricsFormFixture()
    {
        grandPrix = new ActivityEntity { Name = "Grand Prix", GameId = kart.Id };
        timeTrial = new ActivityEntity { Name = "Time Trial", GameId = kart.Id };
        // A past Event: metrics are recorded after the night was played.
        past = new EventEntity
        {
            Name = "Past night",
            Date = new DateTimeOffset(2025, 3, 14, 19, 0, 0, TimeSpan.Zero),
            UniqueGamesOnly = false,
            Selections = [Pick(kart, grandPrix), Pick(kart, timeTrial)],
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

    // The API's answer for a metrics row: the body (eTag included, as the API sends it) and the ETag header.
    protected HttpResponseMessage Row(int? attendees, int? newAttendees, int? returning, decimal? amount, string? eTag, Guid? eventId = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { id = eventId ?? past.Id, attendees, newAttendees, returningAttendees = returning, amountSpent = amount, eTag })
        };
        if (eTag is not null) response.Headers.TryAddWithoutValidation("ETag", eTag);
        return response;
    }

    protected void Stored(int? attendees, int? newAttendees, int? returning, decimal? amount, string? eTag) =>
        api.On(HttpMethod.Get, metricsPath, () => Row(attendees, newAttendees, returning, amount, eTag));

    // The four metrics in a request body, as written (null for a cleared one), in form order.
    protected static List<string> MetricsIn(string? body)
    {
        using var json = JsonDocument.Parse(body!);
        return Metrics.Select(m => json.RootElement.GetProperty(m.JsonName).GetRawText()).ToList();
    }

    // What the four fields show, in form order (empty for an empty field).
    protected static List<string> Values(IRenderedComponent<Events> page) => Metrics.Select(m => Value(page, m.Label) ?? "").ToList();

    protected async Task<IRenderedComponent<Events>> OpenDetails()
    {
        var page = harness.Render<Events>();
        page.WaitForRows(1);
        await DetailsButton(page).ClickAsync(new());
        page.WaitForAssertion(() => Assert.IsNotNull(FindField(page, "Attendees")));
        return page;
    }

    protected static IElement DetailsButton(IRenderedComponent<Events> page) => RowButton(page, "Past night", "Details");

    // The text field a visible label names (the label's "for"), or null when the form shows no such field.
    protected static IElement? FindField(IRenderedComponent<Events> page, string label)
    {
        var dialog = page.FormDialog();
        var target = dialog.QuerySelectorAll("label").FirstOrDefault(l => l.TextContent.Trim() == label)?.GetAttribute("for");
        return target is null ? null : dialog.QuerySelector($"[id='{target}']");
    }

    protected static IElement Field(IRenderedComponent<Events> page, string label) =>
        FindField(page, label) ?? throw new AssertFailedException($"No field labelled {label}.");

    protected static string? Value(IRenderedComponent<Events> page, string label) => Field(page, label).GetAttribute("value");

    protected static Task Type(IRenderedComponent<Events> page, string label, string text) =>
        Field(page, label).InputAsync(new ChangeEventArgs { Value = text });

    // The messages shown right under one field.
    protected static List<string> FieldAlerts(IRenderedComponent<Events> page, string label) =>
        Field(page, label).ParentElement!.Alerts();

    protected static async Task SaveMetrics(IRenderedComponent<Events> page)
    {
        await page.FormDialog().Button("Save metrics").ClickAsync(new());
        page.WaitForAssertion(() => Assert.IsFalse(page.FormDialog().Button("Close").IsBusy()));
    }
}
