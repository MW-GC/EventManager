using System.Linq.Expressions;
using System.Net;
using System.Text;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
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

    // ---- Extra field rules (Rules), #127 ---------------------------------------------------------
    // A page gives the editor rules for its own fields. These use a made-up rule on a Website field the
    // test adds itself, so they pin the shared hook and not the Games page's Website rule.

    private const string RuleMessage = "Website must not be bad.";

    private static IEnumerable<LibraryFieldError> NotBad(GameEntity g) =>
        g.Website == "bad" ? [new LibraryFieldError(nameof(GameEntity.Website), RuleMessage)] : [];

    [TestMethod]
    public async Task Rules_AFailingRule_ShowsItsMessageUnderItsField_AndBlocksTheSave()
    {
        var page = RuledGames(NotBad);
        await page.Button("Add Game").ClickAsync(new());
        await page.Find(GameNameField).ChangeAsync(new ChangeEventArgs { Value = "Ghost Race" });
        await RuleField(page).ChangeAsync(new ChangeEventArgs { Value = "bad" });

        await page.FormDialog().QuerySelector("form")!.SubmitAsync();

        Assert.AreEqual("Add Game", page.DialogTitle());
        Assert.AreEqual(RuleMessage, Assert.ContainsSingle(page.FormDialog().Alerts()));
        Assert.AreEqual(RuleMessage, Assert.ContainsSingle(RuleField(page).ParentElement!.Alerts()));
        Assert.IsEmpty(api.Requests.Where(r => r.Method != HttpMethod.Get).ToList());
    }

    [TestMethod]
    public async Task Rules_APassingRule_LetsTheSaveThrough()
    {
        var page = RuledGames(NotBad);
        api.Status(HttpMethod.Post, "/api/games", HttpStatusCode.Created);
        await page.Button("Add Game").ClickAsync(new());
        await page.Find(GameNameField).ChangeAsync(new ChangeEventArgs { Value = "Ghost Race" });
        await RuleField(page).ChangeAsync(new ChangeEventArgs { Value = "good" });

        await page.FormDialog().QuerySelector("form")!.SubmitAsync();

        Assert.AreEqual("good", Json(Assert.ContainsSingle(api.Bodies(HttpMethod.Post, "/api/games"))).GetProperty("website").GetString());
        Assert.IsFalse(page.HasFormDialog());
    }

    [TestMethod]
    public async Task Rules_ChangingTheField_ClearsItsMessage_AndTheSaveThenSends()
    {
        var page = RuledGames(NotBad);
        api.Status(HttpMethod.Post, "/api/games", HttpStatusCode.Created);
        await page.Button("Add Game").ClickAsync(new());
        await page.Find(GameNameField).ChangeAsync(new ChangeEventArgs { Value = "Ghost Race" });
        await RuleField(page).ChangeAsync(new ChangeEventArgs { Value = "bad" });
        await page.FormDialog().QuerySelector("form")!.SubmitAsync();
        Assert.AreEqual(RuleMessage, Assert.ContainsSingle(page.FormDialog().Alerts()));

        // Changing another field leaves the message; changing its own field checks it again.
        await page.Find(GameNameField).InputAsync(new ChangeEventArgs { Value = "Ghost Racer" });
        Assert.AreEqual(RuleMessage, Assert.ContainsSingle(page.FormDialog().Alerts()));
        await RuleField(page).InputAsync(new ChangeEventArgs { Value = "fine" });
        Assert.IsEmpty(page.FormDialog().Alerts());

        await page.FormDialog().QuerySelector("form")!.SubmitAsync();
        Assert.AreEqual(1, api.Count(HttpMethod.Post, "/api/games"));
        Assert.IsFalse(page.HasFormDialog());
    }

    [TestMethod]
    public async Task Rules_AndTheNameRule_BothRunOnSubmit_EachMessageUnderItsOwnField()
    {
        var page = RuledGames(NotBad);
        await page.Button("Add Game").ClickAsync(new());
        await RuleField(page).ChangeAsync(new ChangeEventArgs { Value = "bad" });

        await page.FormDialog().QuerySelector("form")!.SubmitAsync();

        CollectionAssert.AreEqual(new[] { "Name is required.", RuleMessage }, page.FormDialog().Alerts());
        Assert.AreEqual("Name is required.", Assert.ContainsSingle(page.Find(GameNameField).ParentElement!.Alerts()));
        Assert.AreEqual(RuleMessage, Assert.ContainsSingle(RuleField(page).ParentElement!.Alerts()));
        Assert.IsEmpty(api.Requests.Where(r => r.Method != HttpMethod.Get).ToList());
    }

    // ---- The Games Website (#127): the API's rule, and old values that break it --------------------

    private const string WebsiteMessage = "Website must be an http or https address.";
    private const string WebsiteField = "fluent-dialog fluent-text-field[placeholder^='https://']";

    [TestMethod]
    [DataRow("ftp://x")]
    [DataRow("javascript:alert(1)")]
    [DataRow("example.com")]
    public async Task GamesAdd_RefusesANonHttpWebsite_WithTheApisMessage_AndSendsNothing(string website)
    {
        var page = Games();
        await page.Button("Add Game").ClickAsync(new());
        await page.Find(GameNameField).ChangeAsync(new ChangeEventArgs { Value = "Ghost Race" });
        await page.Find(WebsiteField).ChangeAsync(new ChangeEventArgs { Value = website });

        await page.FormDialog().QuerySelector("form")!.SubmitAsync();

        Assert.AreEqual("Add Game", page.DialogTitle());
        Assert.AreEqual(WebsiteMessage, Assert.ContainsSingle(page.FormDialog().Alerts()));
        Assert.AreEqual(WebsiteMessage, Assert.ContainsSingle(page.Find(WebsiteField).ParentElement!.Alerts()));
        Assert.IsEmpty(api.Requests.Where(r => r.Method != HttpMethod.Get).ToList());
    }

    [TestMethod]
    [DataRow("https://store.steampowered.com/app/1")]
    [DataRow("HTTP://example.test")]
    [DataRow("")]
    public async Task GamesAdd_AnHttpOrEmptyWebsite_Saves(string website)
    {
        var page = Games();
        api.Status(HttpMethod.Post, "/api/games", HttpStatusCode.Created);
        await page.Button("Add Game").ClickAsync(new());
        await page.Find(GameNameField).ChangeAsync(new ChangeEventArgs { Value = "Ghost Race" });
        await page.Find(WebsiteField).ChangeAsync(new ChangeEventArgs { Value = website });

        await page.FormDialog().QuerySelector("form")!.SubmitAsync();

        Assert.AreEqual(website, Json(Assert.ContainsSingle(api.Bodies(HttpMethod.Post, "/api/games"))).GetProperty("website").GetString());
        Assert.IsFalse(page.HasFormDialog());
    }

    // A Website stored before the rule still loads and shows, as text: only an http(s) value is a link,
    // so a javascript: value can never run from the list.
    [TestMethod]
    public async Task Games_AnOldNonHttpWebsite_ShowsAsTextWithNoLink_InTheGridAndTheCards()
    {
        var page = OldWebsiteGames();

        AssertWebsites(page);
        await page.Find("fluent-button[title='Card view']").ClickAsync(new());
        page.WaitForAssertion(() => Assert.HasCount(3, page.FindAll("fluent-card fluent-card")));
        AssertWebsites(page);

        static void AssertWebsites(IRenderedComponent<Games> page)
        {
            var links = page.FindAll("[href]").Where(a => a.Closest("fluent-dialog") is null).Select(a => a.GetAttribute("href")).ToList();
            CollectionAssert.AreEqual(new[] { "https://example.test/kart" }, links);
            Assert.Contains("javascript:alert(1)", page.Markup);
            Assert.Contains("example.com", page.Markup);
        }
    }

    [TestMethod]
    public async Task GamesEdit_AnOldNonHttpWebsite_IsFlaggedOnSave_NotWhenTheEditorOpens()
    {
        var page = OldWebsiteGames();
        var old = $"/api/games/{OldId}";
        api.Status(HttpMethod.Put, old, HttpStatusCode.NoContent);

        await RowButton(page, "Old site", "Edit").ClickAsync(new());
        Assert.AreEqual("Edit Game", page.DialogTitle());
        Assert.IsEmpty(page.FormDialog().Alerts());
        // Renaming does not touch the Website, so it is still not flagged.
        await page.Find(GameNameField).InputAsync(new ChangeEventArgs { Value = "Old site renamed" });
        Assert.IsEmpty(page.FormDialog().Alerts());

        await page.FormDialog().QuerySelector("form")!.SubmitAsync();
        Assert.AreEqual(WebsiteMessage, Assert.ContainsSingle(page.FormDialog().Alerts()));
        Assert.AreEqual(0, api.Count(HttpMethod.Put, old));

        await page.Find(WebsiteField).InputAsync(new ChangeEventArgs { Value = "https://example.com" });
        Assert.IsEmpty(page.FormDialog().Alerts());
        await page.FormDialog().QuerySelector("form")!.SubmitAsync();
        Assert.AreEqual("https://example.com", Json(Assert.ContainsSingle(api.Bodies(HttpMethod.Put, old))).GetProperty("website").GetString());
    }

    [TestMethod]
    public async Task GamesEdit_TouchingAnOldNonHttpWebsite_FlagsItBeforeTheSave()
    {
        var page = OldWebsiteGames();

        await RowButton(page, "Old site", "Edit").ClickAsync(new());
        Assert.IsEmpty(page.FormDialog().Alerts());
        await page.Find(WebsiteField).InputAsync(new ChangeEventArgs { Value = "example.co" });

        Assert.AreEqual(WebsiteMessage, Assert.ContainsSingle(page.FormDialog().Alerts()));
        Assert.IsEmpty(api.Requests.Where(r => r.Method != HttpMethod.Get).ToList());
    }

    // The form's Website rule is the API's own (FieldRules.HttpAddress), value for value.
    [TestMethod]
    public void WebsiteRule_MatchesTheApi()
    {
        foreach (var website in new[] { null, "", "   ", "http://x", "https://x", "HTTPS://x", "https://store.steampowered.com/app/1",
                     "ftp://x", "javascript:alert(1)", "example.com", "/games/1", "mailto:a@b.test", "file:///etc/passwd", "not a url" })
            Assert.AreEqual(FieldRules.HttpAddress(website, "Website"), LibraryRules.Website(website), $"website {website}");
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

    private static readonly Guid OldId = Guid.Parse("1a2b3c4d-5e6f-4a5b-8c7d-9e0f1a2b3c4d");

    // The Games page over a library with one good Website and two stored before the rule.
    private IRenderedComponent<Games> OldWebsiteGames()
    {
        api.Json(HttpMethod.Get, "/api/games", new[]
        {
            new GameEntity { Name = "Kart", Website = "https://example.test/kart" },
            new GameEntity { Id = OldId, Name = "Old site", Website = "example.com" },
            new GameEntity { Name = "Script", Website = "javascript:alert(1)" },
        });
        var page = harness.Render<Games>();
        page.WaitForRows(3);
        return page;
    }

    // The shared page for Games with a Website field the test adds itself and the rules it is given.
    private IRenderedComponent<LibraryPage<GameEntity>> RuledGames(Func<GameEntity, IEnumerable<LibraryFieldError>> rules)
    {
        RenderFragment<LibraryEdit<GameEntity>> website = edit => b =>
        {
            b.OpenElement(0, "div");
            b.OpenComponent<FluentTextField>(1);
            b.AddComponentParameter(2, nameof(FluentTextField.Value), edit.Item.Website);
            b.AddComponentParameter(3, nameof(FluentTextField.ValueChanged), EventCallback.Factory.Create<string?>(this, v => edit.Item.Website = v));
            b.AddComponentParameter(4, nameof(FluentTextField.ValueExpression), (Expression<Func<string?>>)(() => edit.Item.Website));
            b.AddComponentParameter(5, nameof(FluentTextField.Immediate), true);
            b.AddComponentParameter(6, nameof(FluentTextField.Placeholder), "rule-field");
            b.CloseComponent();
            b.OpenComponent<LibraryFieldMessage>(7);
            b.AddComponentParameter(8, nameof(LibraryFieldMessage.Field), nameof(GameEntity.Website));
            b.CloseComponent();
            b.CloseElement();
        };
        var page = harness.Render<LibraryPage<GameEntity>>(p => p
            .Add(x => x.Title, "Games")
            .Add(x => x.Singular, "Game")
            .Add(x => x.Client, new LibraryClient<GameEntity>(api.Client(), "games"))
            .Add(x => x.Name, (Expression<Func<GameEntity, string>>)(g => g.Name))
            .Add(x => x.NewItem, () => new GameEntity { Name = string.Empty })
            .Add(x => x.CopyItem, g => new GameEntity { Id = g.Id, Name = g.Name, Website = g.Website })
            .Add(x => x.EditorFields, website)
            .Add(x => x.Rules, rules));
        page.WaitForRows(1);
        return page;
    }

    private static IElement RuleField<T>(IRenderedComponent<T> page) where T : IComponent =>
        page.Find("fluent-dialog fluent-text-field[placeholder='rule-field']");

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
