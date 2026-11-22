using System.Net;
using System.Text;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components.Icons.Regular;
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

    // The client throws for a non-success status too (#159), not only when the server cannot be reached: the
    // page shows the server's message in the same error bar, with Retry.
    [TestMethod]
    public async Task FailedLoad_AServerError_ShowsTheServerMessage_AndRetryLoadsTheList()
    {
        api.Status(HttpMethod.Get, "/api/themes", HttpStatusCode.InternalServerError);

        var page = Themes(rows: 0);
        page.WaitForPageAlert();

        Assert.AreEqual("Themes could not be loaded. The server had a problem (HTTP 500). Try again.", Assert.ContainsSingle(page.PageAlerts()));
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

    // A page that gives neither a card template nor a card icon (the bare page, and Activities, which never had
    // cards) gets no view toggle; Games (its own template) and a page with a card icon get both buttons.
    [TestMethod]
    public void CardView_IsOfferedOnlyToAPageThatGivesACardTemplate()
    {
        var page = Themes();
        Assert.IsEmpty(page.FindAll("fluent-button[title='Card view']"));
        Assert.IsEmpty(page.FindAll("fluent-button[title='Table view']"));

        var activities = harness.Render<Activities>();
        activities.WaitForAssertion(() => Assert.IsNotEmpty(activities.FindAll("fluent-search")));
        Assert.IsEmpty(activities.FindAll("fluent-button[title='Card view']"));
        Assert.IsEmpty(activities.FindAll("fluent-button[title='Table view']"));

        var games = Games();
        Assert.ContainsSingle(games.FindAll("fluent-button[title='Card view']"));
        Assert.ContainsSingle(games.FindAll("fluent-button[title='Table view']"));

        var iconCards = Themes(cardIcon: new Size24.Color());
        Assert.ContainsSingle(iconCards.FindAll("fluent-button[title='Card view']"));
    }

    // The card view's Edit and Delete are the page's own: Edit opens that item, Delete asks and deletes it, and
    // while a delete runs every card's Delete and the busy card's Edit are disabled, as in the table.
    [TestMethod]
    public async Task CardView_EditAndDeleteFromACard_Work_AndAreDisabledWhileADeleteRuns()
    {
        var page = Themes(cardIcon: new Size24.Color());
        await page.Find("fluent-button[title='Card view']").ClickAsync(new());
        Assert.IsEmpty(page.Rows());

        await CardButton(page, "Cozy", "Edit").ClickAsync(new());
        Assert.AreEqual("Edit Theme", page.DialogTitle());
        Assert.AreEqual("Cozy", NameField(page).GetAttribute("value"));
        await page.FormDialog().Button("Cancel").ClickAsync(new());

        var path = $"/api/themes/{spooky.Id}";
        var gate = api.Hold(HttpMethod.Delete, path, () => new HttpResponseMessage(HttpStatusCode.NoContent));
        api.Json(HttpMethod.Get, "/api/themes", new[] { cozy });
        await CardButton(page, "Spooky", "Delete").ClickAsync(new());
        Assert.AreEqual("Delete Spooky? This cannot be undone.", Message(page.Confirmation()));
        var first = page.ConfirmAsync();

        page.WaitForAssertion(() =>
        {
            Assert.IsTrue(CardButton(page, "Spooky", "Delete").IsBusy());
            Assert.IsTrue(CardButton(page, "Cozy", "Delete").IsBusy());
            Assert.IsTrue(CardButton(page, "Spooky", "Edit").IsBusy());
            Assert.IsFalse(CardButton(page, "Cozy", "Edit").IsBusy());
        });
        // A click that lands anyway asks nothing.
        await CardButton(page, "Cozy", "Delete").ClickAsync(new());
        Assert.IsFalse(page.HasConfirmation());

        gate.SetResult();
        await first;

        Assert.AreEqual(1, api.Count(HttpMethod.Delete, path));
        Assert.HasCount(1, api.Requests.Where(r => r.Method == HttpMethod.Delete).ToList());
        page.WaitForAssertion(() => Assert.IsEmpty(page.FindAll("fluent-card fluent-card").Where(c => c.TextContent.Contains("Spooky")).ToList()));
        Assert.IsFalse(CardButton(page, "Cozy", "Delete").IsBusy());
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
            Assert.AreEqual(FieldRules.Name(name, out _), LibraryRules.NameMessage(name), $"name of {name.Length} characters");
    }

    // #159 and #168: each message is tied to its field. The field names the element its messages show in
    // (aria-describedby) and says it is invalid (aria-invalid) only while it has one; the dialog title is a
    // level-2 heading.
    [TestMethod]
    public async Task Editor_TiesEachMessageToItsField_AndTheTitleIsALevel2Heading()
    {
        var page = Games();
        await page.Button("Add Game").ClickAsync(new());

        var title = page.Find("fluent-dialog .fluent-dialog-body > .fluent-typography");
        Assert.AreEqual("heading", title.GetAttribute("role"));
        Assert.AreEqual("2", title.GetAttribute("aria-level"));
        Assert.AreEqual("Add Game", title.TextContent.Trim());

        Assert.IsFalse(page.Find(GameNameField).HasAttribute("aria-invalid"));
        Assert.IsFalse(page.Find(WebsiteField).HasAttribute("aria-invalid"));
        await page.Find(WebsiteField).ChangeAsync(new ChangeEventArgs { Value = "ftp://x" });
        await page.FormDialog().QuerySelector("form")!.SubmitAsync();

        AssertTied(page.Find(GameNameField), "Name is required.");
        AssertTied(page.Find(WebsiteField), WebsiteMessage);

        // Fixing a field takes its message away and with it aria-invalid; the other field keeps both.
        await page.Find(GameNameField).InputAsync(new ChangeEventArgs { Value = "Ghost Race" });
        Assert.IsFalse(page.Find(GameNameField).HasAttribute("aria-invalid"));
        Assert.IsEmpty(page.Find($"#{page.Find(GameNameField).GetAttribute("aria-describedby")}").Alerts());
        AssertTied(page.Find(WebsiteField), WebsiteMessage);

        void AssertTied(IElement field, string message)
        {
            Assert.AreEqual("true", field.GetAttribute("aria-invalid"));
            var describedBy = field.GetAttribute("aria-describedby");
            Assert.IsFalse(string.IsNullOrEmpty(describedBy));
            var messages = page.FindAll($"[id='{describedBy}']");
            Assert.AreEqual(message, Assert.ContainsSingle(Assert.ContainsSingle(messages).Alerts()));
            // The message sits right under its own field, not elsewhere in the form.
            Assert.AreSame(field.ParentElement, messages[0].ParentElement);
        }
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
    // #168: other spellings of a dangerous scheme are refused too.
    [DataRow("JavaScript:alert(1)")]
    [DataRow(" javascript:alert(1)")]
    [DataRow("data:text/html,x")]
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

    // #168: the API holds a Website to 2048 characters, checked before the http rule; the form checks the same
    // limit with the API's message, so an over-long http address sends nothing.
    [TestMethod]
    [DataRow(2048, null)]
    [DataRow(2049, "Website must be 2048 characters or fewer.")]
    public async Task GamesAdd_WebsiteLength_IsTheApis2048Limit_WithItsMessage(int length, string? expected)
    {
        var website = "https://x.test/" + new string('u', length - "https://x.test/".Length);
        var page = Games();
        api.Status(HttpMethod.Post, "/api/games", HttpStatusCode.Created);
        await page.Button("Add Game").ClickAsync(new());
        await page.Find(GameNameField).ChangeAsync(new ChangeEventArgs { Value = "Ghost Race" });
        await page.Find(WebsiteField).ChangeAsync(new ChangeEventArgs { Value = website });

        await page.FormDialog().QuerySelector("form")!.SubmitAsync();

        if (expected is null)
        {
            Assert.AreEqual(website, Json(Assert.ContainsSingle(api.Bodies(HttpMethod.Post, "/api/games"))).GetProperty("website").GetString());
            Assert.IsFalse(page.HasFormDialog());
        }
        else
        {
            Assert.AreEqual(expected, Assert.ContainsSingle(page.Find(WebsiteField).ParentElement!.Alerts()));
            Assert.IsEmpty(api.Requests.Where(r => r.Method != HttpMethod.Get).ToList());
        }
    }

    // A Website stored before the rule still loads and shows, as text: only an http(s) value is a link,
    // so a javascript: value can never run from the list. Each sits in its own Game's Website cell and card.
    [TestMethod]
    public async Task Games_AnOldNonHttpWebsite_ShowsAsTextWithNoLink_InTheGridAndTheCards()
    {
        var page = OldWebsiteGames();

        AssertWebsiteText(WebsiteCell(page, "Script"), "javascript:alert(1)");
        AssertWebsiteText(WebsiteCell(page, "Old site"), "example.com");
        Assert.AreEqual("https://example.test/kart", WebsiteCell(page, "Kart").QuerySelector("[href]")!.GetAttribute("href"));

        await page.Find("fluent-button[title='Card view']").ClickAsync(new());
        page.WaitForAssertion(() => Assert.HasCount(3, page.FindAll("fluent-card fluent-card")));
        AssertWebsiteText(Card(page, "Script"), "javascript:alert(1)");
        AssertWebsiteText(Card(page, "Old site"), "example.com");
        Assert.AreEqual("https://example.test/kart", Card(page, "Kart").QuerySelector("[href]")!.GetAttribute("href"));

        static void AssertWebsiteText(IElement holder, string website)
        {
            Assert.IsNull(holder.QuerySelector("[href]"), $"{website} must not be a link");
            Assert.IsNull(holder.QuerySelector("fluent-anchor"), $"{website} must not be a link");
            Assert.Contains(website, holder.TextContent, $"{website} shows as text");
        }

        // The grid's second column is the Website.
        static IElement WebsiteCell(IRenderedComponent<Games> page, string game) =>
            page.FindAll("tbody tr.fluent-data-grid-row").Single(r => r.TextContent.Contains(game)).QuerySelectorAll("td")[1];

        static IElement Card(IRenderedComponent<Games> page, string game) =>
            page.FindAll("fluent-card fluent-card").Single(c => c.TextContent.Contains(game));
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

    // The form's Website rules are the API's own (GameValidator: FieldRules.MaxLength with UrlMaxLength, then
    // FieldRules.HttpAddress), value for value, and the limit is the API's constant.
    [TestMethod]
    public void WebsiteRule_MatchesTheApi()
    {
        Assert.AreEqual(typeof(FieldRules).GetField(nameof(FieldRules.UrlMaxLength))!.GetRawConstantValue(),
            typeof(LibraryRules).GetField(nameof(LibraryRules.WebsiteMaxLength))!.GetRawConstantValue());
        var longHttp = "https://x.test/" + new string('u', 2048 - 15);
        foreach (var website in new[] { null, "", "   ", "http://x", "https://x", "HTTPS://x", "https://store.steampowered.com/app/1",
                     "ftp://x", "javascript:alert(1)", "example.com", "/games/1", "mailto:a@b.test", "file:///etc/passwd", "not a url",
                     "JavaScript:alert(1)", " javascript:alert(1)", "data:text/html,x", longHttp, longHttp + "u", new string('u', 2049) })
            Assert.AreEqual(GameValidator.Validate(new GameEntity { Name = "Kart", Website = website }), LibraryRules.WebsiteMessage(website),
                $"website {website?[..Math.Min(website.Length, 40)]}");
    }

    // ---- The generic client ------------------------------------------------------------------------

    // Each client the app registers (AddLibraryClients, as Program.cs calls it) sends what the service it replaced
    // sent: the same verb, path, headers (no If-Match) and body, call for call. GameService's writes, ThemeService
    // and HolidayService were removed in #125, and ActivityService's read, create, update and delete in #126. The
    // paths are written out per list and the bodies by hand (web-default JSON: camelCase, the entity's fields), so
    // a wrong route in the registration or a changed body fails here.
    private static readonly Guid GameIdOfRace = Guid.Parse("22222222-3333-4444-5555-666666666666");
    private static readonly Guid SpookyId = Guid.Parse("33333333-4444-5555-6666-777777777777");

    // EntityBase's fields, which System.Text.Json writes after the entity's own.
    private const string BaseFields = "\"partitionKey\":\"\",\"rowKey\":\"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0\",\"timestamp\":null,\"eTag\":null,\"id\":\"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0\"}";

    [TestMethod]
    [DataRow("games")]
    [DataRow("themes")]
    [DataRow("holidays")]
    [DataRow("activities")]
    public async Task LibraryClient_SendsExactlyWhatTheServiceItReplacesSends(string list)
    {
        var recorder = new Recorder();
        using var services = new ServiceCollection().AddSingleton(recorder.Client()).AddLibraryClients().BuildServiceProvider();
        switch (list)
        {
            case "games":
                await AssertRequests(services.GetRequiredService<LibraryClient<GameEntity>>(), recorder,
                    new GameEntity { Id = Id, Name = "Kart", Website = "https://example.test/kart" }, "/api/games",
                    "{\"name\":\"Kart\",\"imageUrl\":null,\"website\":\"https://example.test/kart\",\"iconUrl\":null," + BaseFields);
                break;
            case "themes":
                await AssertRequests(services.GetRequiredService<LibraryClient<ThemeEntity>>(), recorder,
                    new ThemeEntity { Id = Id, Name = "Spooky" }, "/api/themes",
                    "{\"name\":\"Spooky\"," + BaseFields);
                break;
            case "holidays":
                await AssertRequests(services.GetRequiredService<LibraryClient<HolidayEntity>>(), recorder,
                    new HolidayEntity { Id = Id, Name = "Halloween" }, "/api/holidays",
                    "{\"name\":\"Halloween\"," + BaseFields);
                break;
            default:
                await AssertRequests(services.GetRequiredService<LibraryClient<ActivityEntity>>(), recorder,
                    new ActivityEntity { Id = Id, Name = "Race", GameId = GameIdOfRace, ThemeIds = [SpookyId] }, "/api/activities",
                    "{\"gameId\":\"22222222-3333-4444-5555-666666666666\",\"name\":\"Race\",\"description\":\"\",\"rules\":\"\"," +
                    "\"themeIds\":[\"33333333-4444-5555-6666-777777777777\"],\"holidayIds\":[],\"setupRequirements\":\"\"," + BaseFields);
                break;
        }
    }

    private static async Task AssertRequests<T>(LibraryClient<T> client, Recorder recorder, T item, string path, string body) where T : EntityBase
    {
        await client.GetAllAsync();
        Assert.IsNull(await client.CreateAsync(item));
        Assert.IsNull(await client.UpdateAsync(item));
        Assert.IsNull(await client.DeleteAsync(Id));

        var one = $"{path}/0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0";
        CollectionAssert.AreEqual(new[] { $"GET {path} []", $"POST {path} []", $"PUT {one} []", $"DELETE {one} []" },
            recorder.Sent.Select(s => s.Request).ToList());
        CollectionAssert.AreEqual(new string?[] { null, body, body, null }, recorder.Sent.Select(s => s.Body).ToList());
    }

    // Records each request as "METHOD path [header names]" and its body (null when it has none), and answers
    // 200 with JSON null.
    private sealed class Recorder : HttpMessageHandler
    {
        public List<(string Request, string? Body)> Sent { get; } = [];

        public HttpClient Client() => new(this) { BaseAddress = new Uri("https://example.test/") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = string.Join(",", request.Headers.Select(h => h.Key).Order());
            Sent.Add(($"{request.Method} {request.RequestUri!.PathAndQuery} [{headers}]", body));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null", Encoding.UTF8, "application/json") };
        }
    }

    // ---- Helpers -------------------------------------------------------------------------------

    // The shared page with nothing but what every Library list must say, and a card icon when given.
    private IRenderedComponent<LibraryPage<ThemeEntity>> Themes(int rows = 2, Icon? cardIcon = null)
    {
        var page = harness.Render<LibraryPage<ThemeEntity>>(p => p
            .Add(x => x.Title, "Themes")
            .Add(x => x.Singular, "Theme")
            .Add(x => x.Client, new LibraryClient<ThemeEntity>(api.Client(), "themes"))
            .Add(x => x.Name, t => t.Name)
            .Add(x => x.SetName, (t, name) => t.Name = name)
            .Add(x => x.NewItem, () => new ThemeEntity { Name = string.Empty })
            .Add(x => x.CopyItem, t => new ThemeEntity { Id = t.Id, Name = t.Name })
            .Add(x => x.CardIcon, cardIcon));
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
            b.AddComponentParameter(4, nameof(FluentTextField.Field), new FieldIdentifier(edit.Item, nameof(GameEntity.Website)));
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
            .Add(x => x.Name, g => g.Name)
            .Add(x => x.SetName, (g, name) => g.Name = name)
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

    private static IElement CardButton<T>(IRenderedComponent<T> page, string item, string title) where T : IComponent =>
        page.FindAll("fluent-card fluent-card").Single(c => c.TextContent.Contains(item)).QuerySelector($"fluent-button[title={title}]")!;

    private static string Message(IElement dialog) => dialog.QuerySelector(".confirm-message")!.TextContent.Trim();

    private static JsonElement Json(string? body)
    {
        Assert.IsNotNull(body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
