using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Series;
using MW_GC.EventManager.Web.Pages;
using MW_GC.EventManager.Web.Services;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// The Events page over a stub API for the edit scope tests (#100, #204 Web part 2b). The browser's zone is pinned to
// Kathmandu (UTC+05:45, no daylight saving) and the Raid Series is stored at UTC-04:00, a daylight-saving offset, so a
// time of day read at the browser's offset, or at UTC, instead of the Series' own fails on any machine. The stub
// matches on the path; every request's full URI (with its query) is recorded here, in front of it.
public abstract class OccurrenceEditFixture : IDisposable
{
    protected const string RaidTag = "W/\"datetime'2026-11-25T10%3A00%3A00.0000000Z'\"";
    protected const string CupTag = "W/\"datetime'2026-11-24T09%3A00%3A00.0000000Z'\"";
    protected static readonly TimeSpan Kathmandu = new(5, 45, 0);
    protected static readonly TimeSpan RaidOffset = TimeSpan.FromHours(-4);
    protected static readonly TimeSpan CupOffset = TimeSpan.FromHours(1);
    protected static readonly Guid RaidSeries = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    protected static readonly Guid CupSeries = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private protected readonly StubApiHandler api = new();
    protected readonly UriRecorder uris;
    private readonly BunitContext context = new();
    private readonly string? zone = Environment.GetEnvironmentVariable("TZ");
    private readonly CultureInfo culture = CultureInfo.CurrentCulture;

    protected readonly GameEntity kart = new() { Name = "Kart" }, brawl = new() { Name = "Brawl" };
    protected readonly ActivityEntity grandPrix, stock, royale;
    protected readonly EventEntity quiz, raid, cup, empty;
    protected readonly SeriesEntity raidSeries, cupSeries;

    protected OccurrenceEditFixture()
    {
        Environment.SetEnvironmentVariable("TZ", "Asia/Kathmandu");
        TimeZoneInfo.ClearCachedData();
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        grandPrix = new ActivityEntity { Name = "Grand Prix", GameId = kart.Id };
        stock = new ActivityEntity { Name = "Stock", GameId = brawl.Id };
        royale = new ActivityEntity { Name = "Royale", GameId = brawl.Id };
        quiz = new EventEntity { Name = "Quiz night", Date = new DateTimeOffset(2026, 10, 1, 19, 0, 0, TimeSpan.Zero), Selections = [Pick(kart, grandPrix)] };
        // 15 October 19:00 at the Series' UTC-04:00 is 16 October 04:45 in the browser's Kathmandu.
        var raidKey = new DateTimeOffset(2026, 10, 15, 19, 0, 0, RaidOffset);
        raid = new EventEntity { Name = "Weekly raid", Date = raidKey, SeriesId = RaidSeries, OccurrenceKey = raidKey, Selections = [Pick(kart, grandPrix), Pick(brawl, stock)] };
        // An Occurrence of the Raid Series not rolled yet: no Selections (ADR 0002).
        var emptyKey = new DateTimeOffset(2026, 10, 22, 19, 0, 0, RaidOffset);
        empty = new EventEntity { Name = "Empty night", Date = emptyKey, SeriesId = RaidSeries, OccurrenceKey = emptyKey };
        var cupKey = new DateTimeOffset(2026, 11, 3, 20, 0, 0, CupOffset);
        cup = new EventEntity { Name = "Monthly cup", Date = cupKey, SeriesId = CupSeries, OccurrenceKey = cupKey, UniqueGamesOnly = false, Selections = [Pick(kart, grandPrix)] };
        // Every stored field distinct and set, so a body that drops or swaps one shows it.
        raidSeries = new SeriesEntity
        {
            Id = RaidSeries,
            Name = "Weekly raid",
            Schedule = new RecurrenceSeries(new DateTimeOffset(2026, 9, 24, 19, 0, 0, RaidOffset), RecurrenceRule.Weekly(), new DateTimeOffset(2026, 12, 31, 19, 0, 0, RaidOffset)),
            SlotCount = 4,
            UniqueGamesOnly = true,
            Filters = new SeriesFilters { ThemedOnly = true, GameIds = [kart.Id], ThemeIds = [Guid.Parse("cccccccc-0000-0000-0000-000000000003")], HolidayIds = [Guid.Parse("dddddddd-0000-0000-0000-000000000004")] },
            Skipped = [new DateTimeOffset(2026, 10, 8, 23, 0, 0, TimeSpan.Zero)],
            SplitAt = new DateTimeOffset(2026, 12, 17, 23, 0, 0, TimeSpan.Zero),
            SplitFrom = new DateTimeOffset(2026, 9, 17, 23, 0, 0, TimeSpan.Zero),
        };
        cupSeries = new SeriesEntity
        {
            Id = CupSeries,
            Name = "Monthly cup",
            Schedule = new RecurrenceSeries(new DateTimeOffset(2026, 10, 3, 20, 0, 0, CupOffset), RecurrenceRule.MonthlySameDate()),
            SlotCount = 2,
            UniqueGamesOnly = false,
        };
        api.Json(HttpMethod.Get, "/api/games", new[] { kart, brawl })
           .Json(HttpMethod.Get, "/api/activities", new[] { grandPrix, stock, royale })
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>())
           .Json(HttpMethod.Get, "/api/events", new[] { quiz, raid, cup, empty })
           .On(HttpMethod.Get, SeriesPath(RaidSeries), () => Stored(raidSeries, RaidTag))
           .On(HttpMethod.Get, SeriesPath(CupSeries), () => Stored(cupSeries, CupTag))
           .Status(HttpMethod.Put, SeriesPath(RaidSeries), HttpStatusCode.OK, "{}")
           .Status(HttpMethod.Put, SeriesPath(CupSeries), HttpStatusCode.OK, "{}");
        foreach (var e in new[] { quiz, raid, cup, empty }) api.Status(HttpMethod.Put, EventPath(e), HttpStatusCode.OK, "{}");

        uris = new UriRecorder(api);
        var http = new HttpClient(uris) { BaseAddress = new Uri("https://example.test/") };
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddFluentUIComponents();
        context.Services.AddSingleton(http);
        context.Services.AddLibraryClients();
        context.Services.AddSingleton(new ActivityService(http));
        context.Services.AddSingleton(new EventService(http));
        context.Services.AddSingleton(new SeriesService(http));
        context.Services.AddSingleton(new EventMetricsClient(http));
        context.Services.AddSingleton(new CommentClient(http));
        context.Services.AddScoped<FocusReturn>();
    }

    public void Dispose()
    {
        context.Dispose();
        Environment.SetEnvironmentVariable("TZ", zone);
        TimeZoneInfo.ClearCachedData();
        CultureInfo.CurrentCulture = culture;
        GC.SuppressFinalize(this);
    }

    protected BunitJSInterop JSInterop => context.JSInterop;

    protected static string EventPath(EventEntity e) => $"/api/events/{e.Id}";
    protected static string SeriesPath(Guid series) => $"/api/series/{series}";

    // The Series as GET api/series/{id} answers it: the body and the ETag header.
    protected static HttpResponseMessage Stored(SeriesEntity series, string eTag)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(series, JsonSerializerOptions.Web), Encoding.UTF8, "application/json") };
        response.Headers.TryAddWithoutValidation("ETag", eTag);
        return response;
    }

    // The Events page, loaded, with the edit dialog of the row named so open.
    protected async Task<IRenderedComponent<Events>> OpenEdit(string row)
    {
        if (TimeZoneInfo.Local.BaseUtcOffset != Kathmandu) Assert.Inconclusive("This platform does not take the local zone from TZ.");
        var page = context.Render<Events>();
        page.WaitForRows(4);
        await RowButton(page, row, "Edit").ClickAsync(new());
        Assert.AreEqual("Edit Event", page.DialogTitle());
        return page;
    }

    // Every request but the page's first loads, in order, with its query: "GET /api/series/...", "PUT /api/events/...".
    protected List<string> Sent() => uris.Sent.Where(s => !s.StartsWith("GET /api/") || s.StartsWith("GET /api/series/")).ToList();

    protected static Task Rename(IRenderedComponent<Events> page, string name) =>
        page.FormDialog().QuerySelector("fluent-text-field[placeholder='Enter event name...']")!.InputAsync(new ChangeEventArgs { Value = name });

    protected static Task TypeTime(IRenderedComponent<Events> page, string time) =>
        page.FormDialog().QuerySelector("fluent-text-field.fluent-timepicker")!.InputAsync(new ChangeEventArgs { Value = time });

    protected static Task TypeDate(IRenderedComponent<Events> page, string date) =>
        page.FormDialog().QuerySelector("fluent-text-field.fluent-datepicker")!.ChangeAsync(new ChangeEventArgs { Value = date });

    protected static Task SwitchUniqueGames(IRenderedComponent<Events> page, bool on) =>
        page.FormDialog().QuerySelectorAll("fluent-switch").Single(s => s.GetAttribute("aria-label") == "Unique games only")
            .TriggerEventAsync("onswitchcheckedchange", new CheckboxChangeEventArgs { Checked = on });

    protected static Task Update(IRenderedComponent<Events> page) => page.FormDialog().Button("Update Event").ClickAsync(new());

    protected static bool HasScopeQuestion(IRenderedComponent<Events> page) => page.FindAll("fluent-dialog.scope-dialog").Count > 0;
    protected static IElement ScopeQuestion(IRenderedComponent<Events> page) => page.Find("fluent-dialog.scope-dialog");

    // The edit dialog, never a question over it.
    protected static IElement EditDialog(IRenderedComponent<Events> page) =>
        page.FindAll("fluent-dialog").Single(d => !d.ClassList.Contains("confirm-dialog"));

    protected static List<string> Labels(IRenderedComponent<Events> page) =>
        ScopeQuestion(page).QuerySelectorAll("fluent-radio").Select(r => r.QuerySelector(".scope-label")!.TextContent.Trim()).ToList();

    protected static IElement ConfirmButton(IRenderedComponent<Events> page) => ScopeQuestion(page).QuerySelector("fluent-button.scope-confirm")!;

    // Chooses the scope whose visible label is this, as the radio group reports a choice (a click or an arrow key).
    protected static Task Choose(IRenderedComponent<Events> page, string label)
    {
        var radio = ScopeQuestion(page).QuerySelectorAll("fluent-radio").Single(r => r.QuerySelector(".scope-label")!.TextContent.Trim() == label);
        return ScopeQuestion(page).QuerySelector("fluent-radio-group")!
            .TriggerEventAsync("onradiogroupchange", new ChangeEventArgs { Value = radio.GetAttribute("current-value") });
    }

    protected static async Task ChooseAndConfirm(IRenderedComponent<Events> page, string label)
    {
        await Choose(page, label);
        await ConfirmButton(page).ClickAsync(new());
    }

    // The click handler of a button as the page wired it, taken while it is enabled: a click that lands before a re-render.
    protected static EventCallback<MouseEventArgs> Handler(IRenderedComponent<Events> page, Func<IElement, bool> button) =>
        page.FindComponents<FluentButton>().Single(b => button(b.Find("fluent-button"))).Instance.OnClick;

    // The one Series body sent to this route.
    protected SeriesEntity SentSeries(Guid series) =>
        JsonSerializer.Deserialize<SeriesEntity>(Assert.ContainsSingle(api.Bodies(HttpMethod.Put, SeriesPath(series)))!, JsonSerializerOptions.Web)!;

    // Records each request's method and full path with its query, then hands it to the stub.
    protected sealed class UriRecorder(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<string> Sent { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Add($"{request.Method.Method} {request.RequestUri!.PathAndQuery}");
            return base.SendAsync(request, cancellationToken);
        }
    }
}
