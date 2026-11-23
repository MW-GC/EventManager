using System.Net;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using MW_GC.EventManager.Web.Shared.Library;

namespace MW_GC.EventManager.Web.Tests;

// The Games Website (#127): the API's rule in the editor, and old values that break it, which still load and show
// as text. Split out of LibraryPageTests (#196.10).
[TestClass]
public sealed class GamesWebsiteTests : LibraryPageFixture
{
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

        await Submit(page);

        Assert.AreEqual("Add Game", page.DialogTitle());
        Assert.AreEqual(WebsiteMessage, Assert.ContainsSingle(page.FormDialog().Alerts()));
        Assert.AreEqual(WebsiteMessage, Assert.ContainsSingle(page.Find(WebsiteField).ParentElement!.Alerts()));
        AssertNoWrites();
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

        await Submit(page);

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

        await Submit(page);

        if (expected is null)
        {
            Assert.AreEqual(website, Json(Assert.ContainsSingle(api.Bodies(HttpMethod.Post, "/api/games"))).GetProperty("website").GetString());
            Assert.IsFalse(page.HasFormDialog());
        }
        else
        {
            Assert.AreEqual(expected, Assert.ContainsSingle(page.Find(WebsiteField).ParentElement!.Alerts()));
            AssertNoWrites();
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

        await Submit(page);
        Assert.AreEqual(WebsiteMessage, Assert.ContainsSingle(page.FormDialog().Alerts()));
        Assert.AreEqual(0, api.Count(HttpMethod.Put, old));

        await page.Find(WebsiteField).InputAsync(new ChangeEventArgs { Value = "https://example.com" });
        Assert.IsEmpty(page.FormDialog().Alerts());
        await Submit(page);
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
        AssertNoWrites();
    }

    // The form's Website rules are the API's own (GameValidator: FieldRules.MaxLength with UrlMaxLength, then
    // FieldRules.HttpAddress), value for value, and both limits are compared directly with the one in the row.
    [TestMethod]
    [DataRow(2048)]
    public void WebsiteRule_MatchesTheApi(int limit)
    {
        Assert.AreEqual(FieldRules.UrlMaxLength, limit, "the API's limit");
        Assert.AreEqual(LibraryRules.WebsiteMaxLength, limit, "the form's limit");
        var longHttp = "https://x.test/" + new string('u', 2048 - 15);
        foreach (var website in new[] { null, "", "   ", "http://x", "https://x", "HTTPS://x", "https://store.steampowered.com/app/1",
                     "ftp://x", "javascript:alert(1)", "example.com", "/games/1", "mailto:a@b.test", "file:///etc/passwd", "not a url",
                     "JavaScript:alert(1)", " javascript:alert(1)", "data:text/html,x", longHttp, longHttp + "u", new string('u', 2049) })
            Assert.AreEqual(GameValidator.Validate(new GameEntity { Name = "Kart", Website = website }), LibraryRules.WebsiteMessage(website),
                $"website {website?[..Math.Min(website.Length, 40)]}");
    }
}
