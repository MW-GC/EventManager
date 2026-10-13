using System.Linq.Expressions;
using System.Net;
using System.Text;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using MW_GC.EventManager.Web.Services;
using MW_GC.EventManager.Web.Shared.Library;

namespace MW_GC.EventManager.Web.Tests;

// Issue #124: the shared Library page, its generic client and its validated editor, rendered with bUnit
// against a stub API. Most tests render the page with the least a Library list has to configure (a list
// of Themes: title, client, name, and how to make and copy an item), so they show what Themes, Holidays
// and Activities get when they adopt it. The name rules also go through the Games page, the first on it.
[TestClass]
public sealed class LibraryPageTests : IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);
    private static readonly Guid Id = Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");
    private const string GameNameField = "fluent-dialog fluent-text-field[placeholder='Enter game name...']";
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly ThemeEntity spooky = new() { Name = "Spooky" };
    private readonly ThemeEntity cozy = new() { Name = "Cozy" };
    private readonly GameEntity kart = new() { Name = "Kart" };

    public LibraryPageTests()
    {
        api.Json(HttpMethod.Get, "/api/themes", new[] { spooky, cozy })
           .Json(HttpMethod.Get, "/api/games", new[] { kart })
           .Json(HttpMethod.Get, "/api/activities", Array.Empty<ActivityEntity>())
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>());
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    // ---- Load, error and retry -------------------------------------------------------------------

    [TestMethod]
    public async Task FailedLoad_ShowsTheMessageNotTheSpinner_AndRetryLoadsTheList()
    {
        api.Offline(HttpMethod.Get, "/api/themes");

        var page = Themes(rows: 0);
        page.WaitForPageAlert();

        Assert.AreEqual("Themes could not be loaded. Could not reach the server. Check the connection and try again.", Assert.ContainsSingle(page.PageAlerts()));
        Assert.DoesNotContain("Loading themes...", page.Markup);
        Assert.IsEmpty(page.FindAll("fluent-progress-ring"));
        Assert.IsEmpty(page.Rows());

        api.Json(HttpMethod.Get, "/api/themes", new[] { spooky, cozy });
        await page.Button("Retry").ClickAsync(new());

        page.WaitForRows(2);
        Assert.IsEmpty(page.PageAlerts());
    }

    // ---- Search and card view --------------------------------------------------------------------

    [TestMethod]
    public async Task Search_FiltersByNameIgnoringCase_AndAClearedBoxRestoresTheList()
    {
        var page = Themes();

        await Search(page).InputAsync(new ChangeEventArgs { Value = "SPO" });
        page.WaitForAssertion(() => Assert.Contains("Spooky", Assert.ContainsSingle(page.Rows())), Settle);

        await Search(page).InputAsync(new ChangeEventArgs { Value = "" });
        page.WaitForAssertion(() => Assert.HasCount(2, page.Rows()), Settle);
        Assert.AreEqual("Search themes", Search(page).GetAttribute("aria-label"));
    }

    [TestMethod]
    public void CardView_IsOfferedOnlyToAPageThatGivesACardTemplate()
    {
        var page = Themes();
        Assert.IsEmpty(page.FindAll("fluent-button[title='Card view']"));
        Assert.IsEmpty(page.FindAll("fluent-button[title='Table view']"));

        var games = Games();
        Assert.ContainsSingle(games.FindAll("fluent-button[title='Card view']"));
    }

    // ---- Delete confirmation and the busy guard ----------------------------------------------------

    [TestMethod]
    public async Task Delete_NamesTheItem_CancelKeepsIt_ConfirmRemovesIt()
    {
        var page = Themes();
        var path = $"/api/themes/{spooky.Id}";

        await RowButton(page, "Spooky", "Delete").ClickAsync(new());
        Assert.AreEqual("Delete theme", page.Confirmation().GetAttribute("aria-label"));
        Assert.AreEqual("Delete Spooky? This cannot be undone.", Message(page.Confirmation()));
        await page.Confirmation().Button("Cancel").ClickAsync(new());

        Assert.IsFalse(page.HasConfirmation());
        Assert.IsEmpty(api.Requests.Where(r => r.Method != HttpMethod.Get).ToList());
        Assert.HasCount(2, page.Rows());

        api.Status(HttpMethod.Delete, path, HttpStatusCode.NoContent)
           .Json(HttpMethod.Get, "/api/themes", new[] { cozy });
        await RowButton(page, "Spooky", "Delete").ClickAsync(new());
        await page.ConfirmAsync();

        Assert.AreEqual(1, api.Count(HttpMethod.Delete, path));
        page.WaitForAssertion(() => Assert.Contains("Cozy", Assert.ContainsSingle(page.Rows())));
        Assert.IsFalse(page.HasConfirmation());
        Assert.IsEmpty(page.PageAlerts());
    }

    [TestMethod]
    public async Task Delete_ASecondClickWhileTheFirstIsInFlight_SendsNothing()
    {
        var page = Themes();
        var gate = api.Hold(HttpMethod.Delete, $"/api/themes/{spooky.Id}", () => new HttpResponseMessage(HttpStatusCode.NoContent));

        await RowButton(page, "Spooky", "Delete").ClickAsync(new());
        var first = page.ConfirmAsync();

        // While it runs: every Delete and the busy row's Edit are disabled.
        page.WaitForAssertion(() =>
        {
            Assert.IsTrue(page.FindAll("fluent-button[title=Delete]").All(b => b.IsBusy()));
            Assert.IsTrue(RowButton(page, "Spooky", "Edit").IsBusy());
            Assert.IsFalse(RowButton(page, "Cozy", "Edit").IsBusy());
        });
        // A click that lands anyway asks nothing and sends nothing.
        await RowButton(page, "Cozy", "Delete").ClickAsync(new());
        Assert.IsFalse(page.HasConfirmation());

        gate.SetResult();
        await first;

        Assert.HasCount(1, api.Requests.Where(r => r.Method == HttpMethod.Delete).ToList());
        page.WaitForAssertion(() => Assert.IsFalse(RowButton(page, "Cozy", "Delete").IsBusy()));
    }

    [TestMethod]
    public async Task Save_ASecondSubmitWhileTheFirstIsInFlight_SendsOneRequest()
    {
        var page = Themes();
        var gate = api.Hold(HttpMethod.Post, "/api/themes", () => new HttpResponseMessage(HttpStatusCode.Created));

        await page.Button("Add Theme").ClickAsync(new());
        await NameField(page).ChangeAsync(new ChangeEventArgs { Value = "Retro" });
        var first = page.FormDialog().QuerySelector("form")!.SubmitAsync();
        page.WaitForAssertion(() => Assert.IsTrue(page.FormDialog().Button("Add Theme").IsBusy()));

        await page.FormDialog().QuerySelector("form")!.SubmitAsync();
        gate.SetResult();
        await first;

        Assert.AreEqual(1, api.Count(HttpMethod.Post, "/api/themes"));
        Assert.IsFalse(page.HasFormDialog());
    }

    // ---- Name rules ----------------------------------------------------------------------------

    [TestMethod]
    [DataRow("blank", "Name is required.")]
    [DataRow("spaces", "Name is required.")]
    [DataRow("101", "Name must be 100 characters or fewer.")]
    public async Task GamesAdd_RefusesABadName_WithTheMessage_AndSendsNothing(string kind, string expected)
    {
        var page = Games();
        await page.Button("Add Game").ClickAsync(new());
        await page.Find(GameNameField).ChangeAsync(new ChangeEventArgs { Value = BadName(kind) });

        await page.FormDialog().QuerySelector("form")!.SubmitAsync();

        Assert.AreEqual("Add Game", page.DialogTitle());
        Assert.AreEqual(expected, Assert.ContainsSingle(page.FormDialog().Alerts()));
        Assert.IsEmpty(api.Requests.Where(r => r.Method != HttpMethod.Get).ToList());
        Assert.IsFalse(page.FormDialog().Button("Add Game").IsBusy());
    }

    // In a browser the field's own required check would stop a blank submit with its tooltip before the
    // form saw it, so the name rule's message never showed. The form leaves every check to the name rule;
    // the field still says it is required (asterisk, aria-required).
    [TestMethod]
    public async Task Editor_LeavesTheNameCheckToTheForm_NotTheBrowser()
    {
        var page = Games();
        await page.Button("Add Game").ClickAsync(new());

        Assert.IsTrue(page.FormDialog().QuerySelector("form")!.HasAttribute("novalidate"));
        Assert.IsTrue(page.Find(GameNameField).HasAttribute("required"));
    }

    [TestMethod]
    public async Task GamesEdit_RefusesAWhitespaceName_AndSendsNothing()
    {
        var page = Games();
        await RowButton(page, "Kart", "Edit").ClickAsync(new());
        await page.FormDialog().QuerySelector("fluent-text-field")!.ChangeAsync(new ChangeEventArgs { Value = "   " });

        await page.FormDialog().QuerySelector("form")!.SubmitAsync();

        Assert.AreEqual("Edit Game", page.DialogTitle());
        Assert.AreEqual("Name is required.", Assert.ContainsSingle(page.FormDialog().Alerts()));
        Assert.AreEqual(0, api.Count(HttpMethod.Put, $"/api/games/{kart.Id}"));
        Assert.Contains("Kart", Assert.ContainsSingle(page.Rows()));
    }

    [TestMethod]
    public async Task GamesAdd_FixingTheName_ClearsTheMessage_AndTheSaveSendsOneRequest()
    {
        var page = Games();
        api.Status(HttpMethod.Post, "/api/games", HttpStatusCode.Created);
        await page.Button("Add Game").ClickAsync(new());
        await page.FormDialog().QuerySelector("form")!.SubmitAsync();
        Assert.AreEqual("Name is required.", Assert.ContainsSingle(page.FormDialog().Alerts()));

        await page.Find(GameNameField).InputAsync(new ChangeEventArgs { Value = "Ghost Race" });
        Assert.IsEmpty(page.FormDialog().Alerts());

        await page.FormDialog().QuerySelector("form")!.SubmitAsync();
        Assert.AreEqual("Ghost Race", Json(Assert.ContainsSingle(api.Bodies(HttpMethod.Post, "/api/games"))).GetProperty("name").GetString());
        Assert.IsFalse(page.HasFormDialog());
    }

    // The API trims before it checks, so a name with spaces around 100 characters is valid in both.
    [TestMethod]
    [DataRow("short")]
    [DataRow("100")]
    [DataRow("padded")]
    public async Task ValidName_SendsOneRequestWithTheNameAsTyped_AndCloses(string kind)
    {
        var name = kind switch
        {
            "100" => new string('x', 100),
            "padded" => "  " + new string('x', 100) + "  ",
            _ => "Retro"
        };
        var page = Themes();
        api.Status(HttpMethod.Post, "/api/themes", HttpStatusCode.Created)
           .Json(HttpMethod.Get, "/api/themes", new[] { spooky, cozy, new ThemeEntity { Name = name.Trim() } });

        await page.Button("Add Theme").ClickAsync(new());
        await NameField(page).ChangeAsync(new ChangeEventArgs { Value = name });
        await page.FormDialog().QuerySelector("form")!.SubmitAsync();

        Assert.AreEqual(name, Json(Assert.ContainsSingle(api.Bodies(HttpMethod.Post, "/api/themes"))).GetProperty("name").GetString());
        Assert.IsFalse(page.HasFormDialog());
        page.WaitForRows(3);
        Assert.IsEmpty(page.PageAlerts());
    }

    [TestMethod]
    public async Task ApiRefusalOfAValidName_ShowsTheApisTextInTheDialog()
    {
        var page = Themes();
        api.Status(HttpMethod.Post, "/api/themes", HttpStatusCode.BadRequest, "Name is already used.");

        await page.Button("Add Theme").ClickAsync(new());
        await NameField(page).ChangeAsync(new ChangeEventArgs { Value = "Spooky" });
        await page.FormDialog().QuerySelector("form")!.SubmitAsync();

        Assert.AreEqual(1, api.Count(HttpMethod.Post, "/api/themes"));
        Assert.AreEqual("Add Theme", page.DialogTitle());
        Assert.AreEqual("Name is already used.", Assert.ContainsSingle(page.FormDialog().Alerts()));
    }

    // The form's limit and messages are the API's own (FieldRules), so the two cannot drift apart. The
    // constants are read as compiled into each assembly, so a change on one side alone fails here.
    [TestMethod]
    public void NameRules_MatchTheApi()
    {
        var api = typeof(FieldRules).GetField(nameof(FieldRules.NameMaxLength))!.GetRawConstantValue();
        var form = typeof(LibraryRules).GetField(nameof(LibraryRules.NameMaxLength))!.GetRawConstantValue();
        Assert.AreEqual(100, api);
        Assert.AreEqual(api, form);
        foreach (var name in new[] { "", "   ", new string('x', 100), new string('x', 101), "  " + new string('x', 100) + "  ", "Kart" })
            Assert.AreEqual(FieldRules.Name(name, out _), LibraryRules.Name(name), $"name of {name.Length} characters");
    }

    // ---- The generic client ------------------------------------------------------------------------

    // The client the other Library pages will move to sends what their own service sends today:
    // the same verb, path, headers (no If-Match) and body, call for call.
    [TestMethod]
    [DataRow("themes")]
    [DataRow("holidays")]
    [DataRow("activities")]
    public async Task LibraryClient_SendsExactlyWhatTheServiceItReplacesSends(string route)
    {
        switch (route)
        {
            case "themes":
                var theme = new ThemeEntity { Id = Id, Name = "Spooky" };
                await AssertSameRequests(route, theme, async h =>
                {
                    var s = new ThemeService(h);
                    await s.GetAllAsync(); (await s.CreateAsync(theme)).Dispose(); (await s.UpdateAsync(theme)).Dispose(); (await s.DeleteAsync(Id)).Dispose();
                });
                break;
            case "holidays":
                var holiday = new HolidayEntity { Id = Id, Name = "Halloween" };
                await AssertSameRequests(route, holiday, async h =>
                {
                    var s = new HolidayService(h);
                    await s.GetAllAsync(); (await s.CreateAsync(holiday)).Dispose(); (await s.UpdateAsync(holiday)).Dispose(); (await s.DeleteAsync(Id)).Dispose();
                });
                break;
            default:
                var activity = new ActivityEntity { Id = Id, Name = "Race", GameId = Guid.NewGuid(), ThemeIds = [Guid.NewGuid()] };
                await AssertSameRequests(route, activity, async h =>
                {
                    var s = new ActivityService(h);
                    await s.GetAllAsync(); (await s.CreateAsync(activity)).Dispose(); (await s.UpdateAsync(activity)).Dispose(); (await s.DeleteAsync(Id)).Dispose();
                });
                break;
        }
    }

    private static async Task AssertSameRequests<T>(string route, T item, Func<HttpClient, Task> service) where T : EntityBase
    {
        var old = new Recorder();
        await service(old.Client());
        var shared = new Recorder();
        var client = new LibraryClient<T>(shared.Client(), route);
        await client.GetAllAsync();
        Assert.IsNull(await client.CreateAsync(item));
        Assert.IsNull(await client.UpdateAsync(item));
        Assert.IsNull(await client.DeleteAsync(Id));

        Assert.HasCount(4, old.Sent);
        CollectionAssert.AreEqual(old.Sent, shared.Sent);
    }

    // Records each request as "METHOD path [header names] body" and answers 200 with JSON null.
    private sealed class Recorder : HttpMessageHandler
    {
        public List<string> Sent { get; } = [];

        public HttpClient Client() => new(this) { BaseAddress = new Uri("https://example.test/") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = string.Join(",", request.Headers.Select(h => h.Key).Order());
            Sent.Add($"{request.Method} {request.RequestUri!.PathAndQuery} [{headers}] {body}");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null", Encoding.UTF8, "application/json") };
        }
    }

    // ---- Helpers -------------------------------------------------------------------------------

    // The shared page with nothing but what every Library list must say.
    private IRenderedComponent<LibraryPage<ThemeEntity>> Themes(int rows = 2)
    {
        var page = harness.Render<LibraryPage<ThemeEntity>>(p => p
            .Add(x => x.Title, "Themes")
            .Add(x => x.Singular, "Theme")
            .Add(x => x.Client, new LibraryClient<ThemeEntity>(api.Client(), "themes"))
            .Add(x => x.Name, (Expression<Func<ThemeEntity, string>>)(t => t.Name))
            .Add(x => x.NewItem, () => new ThemeEntity { Name = string.Empty })
            .Add(x => x.CopyItem, t => new ThemeEntity { Id = t.Id, Name = t.Name }));
        if (rows > 0) page.WaitForRows(rows);
        return page;
    }

    private IRenderedComponent<Games> Games()
    {
        var before = api.Requests.Count;
        var page = harness.Render<Games>();
        // Games, Activities, Themes and Holidays: the counts and tags need all four.
        page.WaitForAssertion(() => Assert.AreEqual(before + 4, api.Requests.Count));
        page.WaitForRows(1);
        return page;
    }

    private static string BadName(string kind) => kind switch
    {
        "spaces" => "   ",
        "101" => new string('x', 101),
        _ => string.Empty
    };

    private static IElement NameField<T>(IRenderedComponent<T> page) where T : IComponent =>
        page.FormDialog().QuerySelector("fluent-text-field")!;

    private static IElement Search<T>(IRenderedComponent<T> page) where T : IComponent =>
        page.FindAll("fluent-search").Single(s => s.Closest("fluent-dialog") is null);

    private static IElement RowButton<T>(IRenderedComponent<T> page, string item, string title) where T : IComponent =>
        page.FindAll("tbody tr.fluent-data-grid-row")
            .Where(r => r.Closest("fluent-dialog") is null)
            .Single(r => r.TextContent.Contains(item))
            .QuerySelector($"fluent-button[title={title}]")!;

    private static string Message(IElement dialog) => dialog.QuerySelector(".confirm-message")!.TextContent.Trim();

    private static JsonElement Json(string? body)
    {
        Assert.IsNotNull(body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
