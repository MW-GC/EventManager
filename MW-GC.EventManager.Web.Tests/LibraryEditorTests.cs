using System.Net;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Shared.Library;

namespace MW_GC.EventManager.Web.Tests;

// Issue #124: the shared Library page's Add and Edit dialog: the name rule, a page's own field Rules (#127), and
// how each message is tied to its field (#159, #168, #196.2). Split out of LibraryPageTests (#196.10).
[TestClass]
public sealed class LibraryEditorTests : LibraryPageFixture
{
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

        await Submit(page);

        Assert.AreEqual("Add Game", page.DialogTitle());
        Assert.AreEqual(expected, Assert.ContainsSingle(page.FormDialog().Alerts()));
        AssertNoWrites();
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

        await Submit(page);

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
        await Submit(page);
        Assert.AreEqual("Name is required.", Assert.ContainsSingle(page.FormDialog().Alerts()));

        await page.Find(GameNameField).InputAsync(new ChangeEventArgs { Value = "Ghost Race" });
        Assert.IsEmpty(page.FormDialog().Alerts());

        await Submit(page);
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
        await Submit(page);

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
        await Submit(page);

        Assert.AreEqual(1, api.Count(HttpMethod.Post, "/api/themes"));
        Assert.AreEqual("Add Theme", page.DialogTitle());
        Assert.AreEqual("Name is already used.", Assert.ContainsSingle(page.FormDialog().Alerts()));
    }

    // The form's limit and messages are the API's own (FieldRules), so the two cannot drift apart: both constants
    // are compared directly with the limit in the row, so a change on one side alone fails.
    [TestMethod]
    [DataRow(100)]
    public void NameRules_MatchTheApi(int limit)
    {
        Assert.AreEqual(FieldRules.NameMaxLength, limit, "the API's limit");
        Assert.AreEqual(LibraryRules.NameMaxLength, limit, "the form's limit");
        foreach (var name in new[] { "", "   ", new string('x', 100), new string('x', 101), "  " + new string('x', 100) + "  ", "Kart" })
            Assert.AreEqual(FieldRules.Name(name, out _), LibraryRules.NameMessage(name), $"name of {name.Length} characters");
    }

    // ---- Each message tied to its field (#159, #168, #196.2) ---------------------------------------

    // The field names the element its messages show in (aria-describedby) and says it is invalid (aria-invalid) only
    // while it has one; the dialog title is a level-2 heading.
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
        await Submit(page);

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

    // #196.2: the text field copies aria-describedby into its shadow root, where the id cannot be found (a real
    // browser gave the field no description). Each message holder therefore asks the page's script to link the
    // field's inner input to it by element. bUnit has no shadow root, so this pins what it can see: one link call per
    // field, naming the holder that the field's aria-describedby names. The browser check is in the PR evidence.
    [TestMethod]
    public async Task FieldMessage_LinksItsMessagesToTheFieldsInput()
    {
        var module = harness.JSInterop.SetupModule(LibraryFieldMessage.Module);
        var page = Games();
        await page.Button("Add Game").ClickAsync(new());

        var linked = module.Invocations["link"].Select(i => (string)i.Arguments[0]!).ToList();
        var named = new[] { GameNameField, WebsiteField }.Select(f => page.Find(f).GetAttribute("aria-describedby")!).ToList();
        CollectionAssert.AreEquivalent(new[] { "library-field-Name-message", "library-field-Website-message" }, linked);
        CollectionAssert.AreEquivalent(named, linked);
        foreach (var id in linked) Assert.ContainsSingle(page.FindAll($"[id='{id}']"));
    }

    // #196.13: a closed editor's form no longer re-renders the page when its validation state changes: the page
    // lets go of the form it watched.
    [TestMethod]
    public async Task Editor_AClosedEditorsForm_NoLongerRendersThePage()
    {
        var page = Themes();
        await page.Button("Add Theme").ClickAsync(new());
        var form = page.FindComponent<EditForm>().Instance.EditContext!;
        await page.FormDialog().Button("Cancel").ClickAsync(new());
        Assert.IsFalse(page.HasFormDialog());

        var renders = page.RenderCount;
        await page.InvokeAsync(form.NotifyValidationStateChanged);
        Assert.AreEqual(renders, page.RenderCount);
    }

    // ---- Extra field rules (Rules), #127 ---------------------------------------------------------
    // A page gives the editor rules for its own fields. These use a made-up rule on a Website field the
    // test adds itself, so they pin the shared hook and not the Games page's Website rule.

    private const string RuleMessage = "Website must not be bad.";

    private static IEnumerable<LibraryFieldError> NotBad(GameEntity g) =>
        g.Website == "bad" ? [new LibraryFieldError(new LibraryField(nameof(GameEntity.Website)), RuleMessage)] : [];

    [TestMethod]
    public async Task Rules_AFailingRule_ShowsItsMessageUnderItsField_AndBlocksTheSave()
    {
        var page = RuledGames(NotBad);
        await page.Button("Add Game").ClickAsync(new());
        await page.Find(GameNameField).ChangeAsync(new ChangeEventArgs { Value = "Ghost Race" });
        await RuleField(page).ChangeAsync(new ChangeEventArgs { Value = "bad" });

        await Submit(page);

        Assert.AreEqual("Add Game", page.DialogTitle());
        Assert.AreEqual(RuleMessage, Assert.ContainsSingle(page.FormDialog().Alerts()));
        Assert.AreEqual(RuleMessage, Assert.ContainsSingle(RuleField(page).ParentElement!.Alerts()));
        AssertNoWrites();
    }

    [TestMethod]
    public async Task Rules_APassingRule_LetsTheSaveThrough()
    {
        var page = RuledGames(NotBad);
        api.Status(HttpMethod.Post, "/api/games", HttpStatusCode.Created);
        await page.Button("Add Game").ClickAsync(new());
        await page.Find(GameNameField).ChangeAsync(new ChangeEventArgs { Value = "Ghost Race" });
        await RuleField(page).ChangeAsync(new ChangeEventArgs { Value = "good" });

        await Submit(page);

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
        await Submit(page);
        Assert.AreEqual(RuleMessage, Assert.ContainsSingle(page.FormDialog().Alerts()));

        // Changing another field leaves the message; changing its own field checks it again.
        await page.Find(GameNameField).InputAsync(new ChangeEventArgs { Value = "Ghost Racer" });
        Assert.AreEqual(RuleMessage, Assert.ContainsSingle(page.FormDialog().Alerts()));
        await RuleField(page).InputAsync(new ChangeEventArgs { Value = "fine" });
        Assert.IsEmpty(page.FormDialog().Alerts());

        await Submit(page);
        Assert.AreEqual(1, api.Count(HttpMethod.Post, "/api/games"));
        Assert.IsFalse(page.HasFormDialog());
    }

    [TestMethod]
    public async Task Rules_AndTheNameRule_BothRunOnSubmit_EachMessageUnderItsOwnField()
    {
        var page = RuledGames(NotBad);
        await page.Button("Add Game").ClickAsync(new());
        await RuleField(page).ChangeAsync(new ChangeEventArgs { Value = "bad" });

        await Submit(page);

        CollectionAssert.AreEqual(new[] { "Name is required.", RuleMessage }, page.FormDialog().Alerts());
        Assert.AreEqual("Name is required.", Assert.ContainsSingle(page.Find(GameNameField).ParentElement!.Alerts()));
        Assert.AreEqual(RuleMessage, Assert.ContainsSingle(RuleField(page).ParentElement!.Alerts()));
        AssertNoWrites();
    }
}
