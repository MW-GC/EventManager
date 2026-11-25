using System.Net;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using MW_GC.EventManager.Web.Shared.Activities;
using static MW_GC.EventManager.Web.Tests.PageActions;

namespace MW_GC.EventManager.Web.Tests;

// #98: minimum players, maximum players and duration in minutes on the Activities editor. Each is optional: empty
// is not set, and clearing one sends it as null, which the API stores as unset. The API's rules and messages are
// checked before anything is sent, and each message shows under its field as the other editor fields show theirs.
[TestClass]
public sealed class ActivityPlayersDurationPageTests : IDisposable
{
    private const string Min = "Minimum players";
    private const string Max = "Maximum players";
    private const string Duration = "Duration in minutes";
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly GameEntity kart = new() { Name = "Kart" };
    private readonly ActivityEntity race;
    private readonly ActivityEntity plain;

    public ActivityPlayersDurationPageTests()
    {
        race = new ActivityEntity { Name = "Race", GameId = kart.Id, MinPlayers = 2, MaxPlayers = 6, DurationMinutes = 45 };
        plain = new ActivityEntity { Name = "Plain", GameId = kart.Id };
        api.Json(HttpMethod.Get, "/api/games", new[] { kart })
           .Json(HttpMethod.Get, "/api/activities", new[] { race, plain })
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>())
           .Status(HttpMethod.Post, "/api/activities", HttpStatusCode.Created)
           .Status(HttpMethod.Put, $"/api/activities/{race.Id}", HttpStatusCode.OK)
           .Status(HttpMethod.Put, $"/api/activities/{plain.Id}", HttpStatusCode.OK);
        harness = new PageHarness(api);
    }

    public void Dispose() => harness.Dispose();

    // ---- entering ------------------------------------------------------------------------------

    [TestMethod]
    public async Task Add_WithPlayersAndDuration_SendsThem()
    {
        var page = Open();
        await page.Button("Add Activity").ClickAsync(new());
        await GameOption(page).ClickAsync(new());
        await NameField(page).ChangeAsync(new ChangeEventArgs { Value = "Werewolf" });
        await Type(page, Min, "2");
        await Type(page, Max, "6");
        await Type(page, Duration, "45");

        await Submit(page);

        var sent = Json(Assert.ContainsSingle(api.Bodies(HttpMethod.Post, "/api/activities")));
        Assert.AreEqual(2, sent.GetProperty("minPlayers").GetInt32());
        Assert.AreEqual(6, sent.GetProperty("maxPlayers").GetInt32());
        Assert.AreEqual(45, sent.GetProperty("durationMinutes").GetInt32());
        Assert.IsFalse(page.HasFormDialog());
    }

    [TestMethod]
    public async Task Add_LeavingThemEmpty_SendsNoneSet()
    {
        var page = Open();
        await page.Button("Add Activity").ClickAsync(new());
        await GameOption(page).ClickAsync(new());
        await NameField(page).ChangeAsync(new ChangeEventArgs { Value = "Werewolf" });

        await Submit(page);

        var sent = Json(Assert.ContainsSingle(api.Bodies(HttpMethod.Post, "/api/activities")));
        foreach (var name in new[] { "minPlayers", "maxPlayers", "durationMinutes" })
            Assert.AreEqual(JsonValueKind.Null, sent.GetProperty(name).ValueKind, name);
    }

    // The Edit dialog shows the stored values, and a save that changes something else sends them back unchanged.
    [TestMethod]
    public async Task Edit_ShowsTheStoredValues_AndASaveKeepsThem()
    {
        var page = Open();
        await RowButton(page, "Race", "Edit").ClickAsync(new());

        Assert.AreEqual("2", Value(page, Min));
        Assert.AreEqual("6", Value(page, Max));
        Assert.AreEqual("45", Value(page, Duration));

        await NameField(page).ChangeAsync(new ChangeEventArgs { Value = "Grand race" });
        await Submit(page);

        var sent = Json(Assert.ContainsSingle(api.Bodies(HttpMethod.Put, $"/api/activities/{race.Id}")));
        Assert.AreEqual("Grand race", sent.GetProperty("name").GetString());
        Assert.AreEqual(2, sent.GetProperty("minPlayers").GetInt32());
        Assert.AreEqual(6, sent.GetProperty("maxPlayers").GetInt32());
        Assert.AreEqual(45, sent.GetProperty("durationMinutes").GetInt32());
    }

    [TestMethod]
    public async Task Edit_AnActivityWithNoneSet_ShowsEmptyFields()
    {
        var page = Open();
        await RowButton(page, "Plain", "Edit").ClickAsync(new());

        Assert.AreEqual(string.Empty, Value(page, Min) ?? string.Empty);
        Assert.AreEqual(string.Empty, Value(page, Max) ?? string.Empty);
        Assert.AreEqual(string.Empty, Value(page, Duration) ?? string.Empty);
    }

    // ---- clearing ------------------------------------------------------------------------------

    [TestMethod]
    public async Task Edit_ClearingTheDuration_SendsItUnset_AndKeepsThePlayers()
    {
        var page = Open();
        await RowButton(page, "Race", "Edit").ClickAsync(new());

        await Type(page, Duration, "");
        await Submit(page);

        var sent = Json(Assert.ContainsSingle(api.Bodies(HttpMethod.Put, $"/api/activities/{race.Id}")));
        Assert.AreEqual(JsonValueKind.Null, sent.GetProperty("durationMinutes").ValueKind);
        Assert.AreEqual(2, sent.GetProperty("minPlayers").GetInt32());
        Assert.AreEqual(6, sent.GetProperty("maxPlayers").GetInt32());
        Assert.IsFalse(page.HasFormDialog());
    }

    [TestMethod]
    public async Task Edit_ClearingAField_IsAnUnsavedChange()
    {
        var page = Open();
        await RowButton(page, "Race", "Edit").ClickAsync(new());

        await Type(page, Min, " ");
        await Dismiss(page);

        Assert.IsTrue(page.HasConfirmation(), "Clearing a field must ask before it is discarded.");
    }

    // ---- refusing ------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(Min, "0", "Minimum players must be a whole number from 1 to 99.")]
    [DataRow(Min, "100", "Minimum players must be a whole number from 1 to 99.")]
    [DataRow(Max, "-2", "Maximum players must be a whole number from 1 to 99.")]
    [DataRow(Max, "six", "Maximum players must be a whole number from 1 to 99.")]
    [DataRow(Duration, "0", "Duration in minutes must be a whole number from 1 to 1440.")]
    [DataRow(Duration, "1441", "Duration in minutes must be a whole number from 1 to 1440.")]
    [DataRow(Duration, "2.5", "Duration in minutes must be a whole number from 1 to 1440.")]
    public async Task AValueTheApiRefuses_ShowsItsMessageUnderThatField_AndSendsNothing(string field, string text, string message)
    {
        var page = Open();
        await RowButton(page, "Race", "Edit").ClickAsync(new());

        await Type(page, field, text);
        await Submit(page);

        Assert.AreEqual(message, Assert.ContainsSingle(FieldAlerts(page, field)));
        Assert.AreEqual(message, Assert.ContainsSingle(page.FormDialog().Alerts()));
        Assert.AreEqual("true", Field(page, field).GetAttribute("aria-invalid"));
        AssertNoWrites();
    }

    [TestMethod]
    public async Task MinimumAboveMaximum_ShowsTheMessageUnderTheMinimum_AndSendsNothing()
    {
        var page = Open();
        await RowButton(page, "Race", "Edit").ClickAsync(new());

        await Type(page, Min, "7");
        await Submit(page);

        Assert.AreEqual("Minimum players cannot be more than maximum players.", Assert.ContainsSingle(FieldAlerts(page, Min)));
        Assert.IsEmpty(FieldAlerts(page, Max));
        AssertNoWrites();

        // Fixing it lets the save through.
        await Type(page, Max, "8");
        await Submit(page);
        var sent = Json(Assert.ContainsSingle(api.Bodies(HttpMethod.Put, $"/api/activities/{race.Id}")));
        Assert.AreEqual(7, sent.GetProperty("minPlayers").GetInt32());
        Assert.AreEqual(8, sent.GetProperty("maxPlayers").GetInt32());
    }

    // The form refuses exactly what the API refuses, with the API's message first, at the limits too (99 and 100
    // players, 1440 and 1441 minutes), so a limit or message that drifts on either side fails here.
    [TestMethod]
    public void TheFormsRules_AreTheApisRules()
    {
        int?[] players = [null, -1, 0, 1, 2, 6, 99, 100];
        int?[] durations = [null, 0, 1, 1440, 1441];
        foreach (var min in players)
        foreach (var max in players)
        foreach (var duration in durations)
        {
            var activity = new ActivityEntity { Name = "Act", GameId = Guid.NewGuid(), MinPlayers = min, MaxPlayers = max, DurationMinutes = duration };
            var fields = new PlayersDurationFields(new ActivityEntity { MinPlayers = min, MaxPlayers = max, DurationMinutes = duration });
            Assert.AreEqual(ActivityValidator.Validate(activity), fields.Errors().FirstOrDefault()?.Message, $"min {min}, max {max}, duration {duration}");
        }
    }

    // ---- accessible names ----------------------------------------------------------------------

    [TestMethod]
    [DataRow(Min)]
    [DataRow(Max)]
    [DataRow(Duration)]
    public async Task EachField_HasItsAccessibleName_AndNamesItsMessage(string label)
    {
        var page = Open();
        await page.Button("Add Activity").ClickAsync(new());

        var field = Field(page, label);
        Assert.AreEqual(label, field.GetAttribute("aria-label"));
        var holder = page.Find($"[id='{field.GetAttribute("aria-describedby")}']");
        Assert.AreSame(field.ParentElement, holder.ParentElement);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private IRenderedComponent<Activities> Open()
    {
        var page = harness.Render<Activities>();
        page.WaitForAssertion(() => Assert.AreEqual(4, api.Requests.Count(r => r.Method == HttpMethod.Get)));
        page.WaitForRows(2);
        page.WaitForAssertion(() => Assert.IsFalse(page.Rows().Any(r => r.Contains('—'))));
        return page;
    }

    private void AssertNoWrites() => Assert.IsEmpty(api.Requests.Where(r => r.Method != HttpMethod.Get).ToList());

    private static Task Submit(IRenderedComponent<Activities> page) => page.FormDialog().QuerySelector("form")!.SubmitAsync();

    private static IElement NameField(IRenderedComponent<Activities> page) =>
        page.Find("fluent-dialog fluent-text-field[placeholder='Enter activity name...']");

    private IElement GameOption(IRenderedComponent<Activities> page) =>
        page.FormDialog().QuerySelector("fluent-select")!.QuerySelectorAll("fluent-option").Single(o => o.GetAttribute("value") == kart.Id.ToString());

    // The text field a visible label names (the label's "for").
    private static IElement Field(IRenderedComponent<Activities> page, string label)
    {
        var dialog = page.FormDialog();
        var target = dialog.QuerySelectorAll("label").SingleOrDefault(l => l.TextContent.Trim() == label)?.GetAttribute("for")
            ?? throw new AssertFailedException($"No field labelled {label}.");
        return dialog.QuerySelector($"[id='{target}']")!;
    }

    private static string? Value(IRenderedComponent<Activities> page, string label) => Field(page, label).GetAttribute("value");

    private static Task Type(IRenderedComponent<Activities> page, string label, string text) =>
        Field(page, label).InputAsync(new ChangeEventArgs { Value = text });

    // The messages shown right under one field.
    private static List<string> FieldAlerts(IRenderedComponent<Activities> page, string label) => Field(page, label).ParentElement!.Alerts();

    private static JsonElement Json(string? body)
    {
        Assert.IsNotNull(body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
