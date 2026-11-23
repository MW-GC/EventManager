using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using MW_GC.EventManager.Web.Services;
using MW_GC.EventManager.Web.Shared.Library;

namespace MW_GC.EventManager.Web.Tests;

// Issue #124: the shared Library page and its validated editor, rendered with bUnit against a stub API. What the
// page tests share (#196.10 split them by behaviour: LibraryPageTests, LibraryEditorTests, GamesWebsiteTests): the
// stub with a few Themes and one Game, and the pages they render. Most render the page with the least a Library list
// has to configure (a list of Themes: title, client, name, and how to make and copy an item), so they show what
// Themes, Holidays and Activities get from it; the name and Website rules also go through the Games page.
public abstract class LibraryPageFixture : IDisposable
{
    protected static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);
    protected const string GameNameField = "fluent-dialog fluent-text-field[placeholder='Enter game name...']";
    protected const string WebsiteField = "fluent-dialog fluent-text-field[placeholder^='https://']";
    protected const string WebsiteMessage = "Website must be an http or https address.";
    protected static readonly Guid OldId = Guid.Parse("1a2b3c4d-5e6f-4a5b-8c7d-9e0f1a2b3c4d");

    private protected readonly StubApiHandler api = new();
    private protected readonly PageHarness harness;
    protected readonly ThemeEntity spooky = new() { Name = "Spooky" };
    protected readonly ThemeEntity cozy = new() { Name = "Cozy" };
    protected readonly GameEntity kart = new() { Name = "Kart" };

    protected LibraryPageFixture()
    {
        api.Json(HttpMethod.Get, "/api/themes", new[] { spooky, cozy })
           .Json(HttpMethod.Get, "/api/games", new[] { kart })
           .Json(HttpMethod.Get, "/api/activities", Array.Empty<ActivityEntity>())
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>());
        harness = new PageHarness(api);
    }

    public void Dispose()
    {
        harness.Dispose();
        GC.SuppressFinalize(this);
    }

    // The shared page with nothing but what every Library list must say, and a card icon when given.
    private protected IRenderedComponent<LibraryPage<ThemeEntity>> Themes(int rows = 2, Icon? cardIcon = null)
    {
        var page = harness.Render<LibraryPage<ThemeEntity>>(p => p
            .Add(x => x.Title, "Themes")
            .Add(x => x.Singular, "Theme")
            .Add(x => x.Client, new LibraryClient<ThemeEntity>(api.Client(), "themes"))
            .Add(x => x.Name, new LibraryName<ThemeEntity>(t => t.Name, (t, name) => t.Name = name))
            .Add(x => x.NewItem, () => new ThemeEntity { Name = string.Empty })
            .Add(x => x.CopyItem, t => new ThemeEntity { Id = t.Id, Name = t.Name })
            .Add(x => x.CardIcon, cardIcon));
        if (rows > 0) page.WaitForRows(rows);
        return page;
    }

    private protected IRenderedComponent<Games> Games()
    {
        var before = api.Requests.Count;
        var page = harness.Render<Games>();
        // Games, Activities, Themes and Holidays: the counts and tags need all four.
        page.WaitForAssertion(() => Assert.AreEqual(before + 4, api.Requests.Count));
        page.WaitForRows(1);
        return page;
    }

    // The Games page over a library with one good Website and two stored before the rule.
    private protected IRenderedComponent<Games> OldWebsiteGames()
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
    private protected IRenderedComponent<LibraryPage<GameEntity>> RuledGames(Func<GameEntity, IEnumerable<LibraryFieldError>> rules)
    {
        var field = new LibraryField(nameof(GameEntity.Website));
        RenderFragment<LibraryEdit<GameEntity>> website = edit => b =>
        {
            b.OpenElement(0, "div");
            b.OpenComponent<FluentTextField>(1);
            b.AddComponentParameter(2, nameof(FluentTextField.Value), edit.Item.Website);
            b.AddComponentParameter(3, nameof(FluentTextField.ValueChanged), EventCallback.Factory.Create<string?>(this, v => edit.Item.Website = v));
            b.AddComponentParameter(4, nameof(FluentTextField.Field), field.On(edit.Item));
            b.AddComponentParameter(5, nameof(FluentTextField.Immediate), true);
            b.AddComponentParameter(6, nameof(FluentTextField.Placeholder), "rule-field");
            b.CloseComponent();
            b.OpenComponent<LibraryFieldMessage>(7);
            b.AddComponentParameter(8, nameof(LibraryFieldMessage.Field), field);
            b.CloseComponent();
            b.CloseElement();
        };
        var page = harness.Render<LibraryPage<GameEntity>>(p => p
            .Add(x => x.Title, "Games")
            .Add(x => x.Singular, "Game")
            .Add(x => x.Client, new LibraryClient<GameEntity>(api.Client(), "games"))
            .Add(x => x.Name, new LibraryName<GameEntity>(g => g.Name, (g, name) => g.Name = name))
            .Add(x => x.NewItem, () => new GameEntity { Name = string.Empty })
            .Add(x => x.CopyItem, g => new GameEntity { Id = g.Id, Name = g.Name, Website = g.Website })
            .Add(x => x.EditorFields, website)
            .Add(x => x.Rules, rules));
        page.WaitForRows(1);
        return page;
    }

    private protected void AssertNoWrites() => Assert.IsEmpty(api.Requests.Where(r => r.Method != HttpMethod.Get).ToList());

    protected static IElement RuleField<T>(IRenderedComponent<T> page) where T : IComponent =>
        page.Find("fluent-dialog fluent-text-field[placeholder='rule-field']");

    protected static string BadName(string kind) => kind switch
    {
        "spaces" => "   ",
        "101" => new string('x', 101),
        _ => string.Empty
    };

    protected static Task Submit<T>(IRenderedComponent<T> page) where T : IComponent =>
        page.FormDialog().QuerySelector("form")!.SubmitAsync();

    protected static IElement NameField<T>(IRenderedComponent<T> page) where T : IComponent =>
        page.FormDialog().QuerySelector("fluent-text-field")!;

    protected static IElement Search<T>(IRenderedComponent<T> page) where T : IComponent =>
        page.FindAll("fluent-search").Single(s => s.Closest("fluent-dialog") is null);

    protected static IElement CardButton<T>(IRenderedComponent<T> page, string item, string title) where T : IComponent =>
        page.FindAll("fluent-card fluent-card").Single(c => c.TextContent.Contains(item)).QuerySelector($"fluent-button[title={title}]")!;

    protected static string Message(IElement dialog) => dialog.QuerySelector(".confirm-message")!.TextContent.Trim();

    protected static JsonElement Json(string? body)
    {
        Assert.IsNotNull(body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
