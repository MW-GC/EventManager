using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Pages;

namespace MW_GC.EventManager.Web.Tests;

// Issue #105: Escape pressed on a Fluent UI select inside a form dialog. The browser hands the same
// keydown to the select and to the dialog. Fluent UI 4.14.4's select (ListComponentBase) waits one
// millisecond and then calls its JS module; the dialog raises "dismiss", and a clean form closes.
// If that close removes the select first, the select's JS module is disposed before the call, the
// call throws, and the page falls into the error boundary. bUnit's loose JS interop hands out a module
// that never minds being disposed, so these tests give every select its own module that throws once
// disposed, as the browser's JSObjectReference does.
[TestClass]
public sealed class EscapeOnSelectTests : IDisposable
{
    private readonly StubApiHandler api = new();
    private readonly PageHarness harness;
    private readonly ListModules modules = new();
    private readonly GameEntity kart = new() { Name = "Kart" };
    private readonly GameEntity brawl = new() { Name = "Brawl" };
    private readonly ActivityEntity grandPrix;
    private readonly ActivityEntity timeTrial;
    private readonly ActivityEntity stock;
    private readonly EventEntity night;

    public EscapeOnSelectTests()
    {
        grandPrix = new ActivityEntity { Name = "Grand Prix", GameId = kart.Id };
        timeTrial = new ActivityEntity { Name = "Time Trial", GameId = kart.Id };
        stock = new ActivityEntity { Name = "Stock", GameId = brawl.Id };
        night = new EventEntity
        {
            Name = "Game night",
            Date = new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero),
            UniqueGamesOnly = false,
            Selections = [Pick(kart, grandPrix), Pick(brawl, stock)]
        };
        api.Json(HttpMethod.Get, "/api/games", new[] { kart, brawl })
           .Json(HttpMethod.Get, "/api/activities", new[] { grandPrix, timeTrial, stock })
           .Json(HttpMethod.Get, "/api/themes", Array.Empty<ThemeEntity>())
           .Json(HttpMethod.Get, "/api/holidays", Array.Empty<HolidayEntity>())
           .Json(HttpMethod.Get, "/api/events", new[] { night });
        harness = new PageHarness(api);
        harness.JSInterop.AddInvocationHandler(modules);
    }

    public void Dispose() => harness.Dispose();

    private static Selection Pick(GameEntity game, ActivityEntity act) => new()
    {
        Game = new Game { Id = game.Id, Name = game.Name },
        Activity = new Activity { Id = act.Id, GameId = act.GameId, Name = act.Name }
    };

    [TestMethod]
    [DataRow("activities-create")]
    [DataRow("events-create")]
    [DataRow("events-edit")]
    public async Task Escape_OnSelectInCleanDialog_ClosesWithoutError(string dialog)
    {
        var page = await OpenDialog(dialog);

        await EscapeOnSelect(page);

        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        Assert.IsFalse(page.HasConfirmation());
        Assert.IsTrue(modules.Created.Any(m => m.Disposed), $"{dialog}: closing did not dispose a select's module");
        AssertNoError(dialog);
    }

    // A dialog with unsaved changes still asks first (#52), and Discard still closes it at once.
    [TestMethod]
    public async Task Escape_OnSelectInChangedDialog_StillAsksToDiscard()
    {
        var page = await OpenDialog("activities-create");
        await GameSelect(page).QuerySelectorAll("fluent-option")
            .Single(o => o.GetAttribute("value") == brawl.Id.ToString()).ClickAsync(new());

        await EscapeOnSelect(page);

        Assert.IsTrue(page.HasFormDialog());
        Assert.IsTrue(page.HasConfirmation());
        AssertNoError("activities-create changed");
        await page.ConfirmAsync("Discard");
        Assert.IsFalse(page.HasFormDialog());
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private async Task<IRenderedComponent<IComponent>> OpenDialog(string dialog)
    {
        IRenderedComponent<IComponent> page = dialog.StartsWith("activities") ? harness.Render<Activities>() : harness.Render<Events>();
        var row = dialog.StartsWith("activities") ? "Grand Prix" : "Game night";
        page.WaitForAssertion(() => Assert.IsTrue(page.Rows().Any(r => r.Contains(row))));
        switch (dialog)
        {
            case "activities-create": await page.Button("Add Activity").ClickAsync(new()); break;
            case "events-create": await page.Button("Create Event").ClickAsync(new()); break;
            case "events-edit": await RowButton(page, "Game night", "Edit").ClickAsync(new()); break;
            default: throw new ArgumentOutOfRangeException(nameof(dialog), dialog);
        }
        Assert.IsTrue(page.HasFormDialog());
        Assert.IsFalse(page.HasConfirmation());
        // Every select on the page, in the dialog or not, has imported its module.
        Assert.IsNotNull(GameSelect(page));
        page.WaitForAssertion(() => Assert.AreEqual(page.FindAll("fluent-select").Count, modules.Created.Count(m => !m.Disposed)));
        return page;
    }

    // What the browser does with one Escape on a select: in the same key event, the select's keydown
    // handler runs and starts its wait, then the dialog raises "dismiss" (measured order, see PR #105).
    // Both are raised in one turn of the renderer's dispatcher, as in the browser, so nothing the
    // select waits for can run in between. Returns when both handlers have finished.
    private static Task EscapeOnSelect(IRenderedComponent<IComponent> page)
    {
        var select = GameSelect(page);
        var dialog = page.FormDialog();
        return page.InvokeAsync(() => Task.WhenAll(
            select.TriggerEventAsync("onkeydown", new KeyboardEventArgs { Key = "Escape", Code = "Escape" }),
            dialog.TriggerEventAsync("ondialogdismiss", new DialogEventArgs { Id = dialog.Id, Reason = "dismiss" })));
    }

    // The first select in the form dialog: the Activities Game picker, or the first slot's Game.
    private static IElement GameSelect(IRenderedComponent<IComponent> page) =>
        page.FormDialog().QuerySelector("fluent-select")!;

    private void AssertNoError(string what)
    {
        var error = harness.UnhandledException;
        Assert.IsFalse(error.IsCompleted, $"{what}: {(error.IsCompleted ? error.Result.ToString() : "")}");
    }

    private static IElement RowButton(IRenderedComponent<IComponent> page, string item, string title) =>
        page.FindAll("tbody tr.fluent-data-grid-row")
            .Where(r => r.Closest("fluent-dialog") is null)
            .Single(r => r.TextContent.Contains(item))
            .QuerySelector($"fluent-button[title={title}]")!;

    // ---- JS modules that mind being disposed ----------------------------------------------------

    // Answers each select's import of ListComponentBase.razor.js (with its ?v= version) with a module of its own.
    private sealed class ListModules() : JSRuntimeInvocationHandlerBase<IJSObjectReference>(
        i => i.Identifier == "import" && i.Arguments.Count == 1 && i.Arguments[0] is string path
             && path.Contains("/List/ListComponentBase.razor.js", StringComparison.Ordinal),
        isCatchAllHandler: false)
    {
        public List<ListModule> Created { get; } = [];

        protected override Task<IJSObjectReference> HandleAsync(JSRuntimeInvocation invocation)
        {
            var module = new ListModule();
            Created.Add(module);
            return Task.FromResult<IJSObjectReference>(module);
        }
    }

    // Like the browser's JSObjectReference: every call after DisposeAsync throws ObjectDisposedException.
    private sealed class ListModule : IJSObjectReference
    {
        public bool Disposed { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);

        public ValueTask<IJSObjectReference> InvokeConstructorAsync(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<IJSObjectReference> InvokeConstructorAsync(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new NotSupportedException();
        public ValueTask<TValue> GetValueAsync<TValue>(string identifier) => throw new NotSupportedException();
        public ValueTask<TValue> GetValueAsync<TValue>(string identifier, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask SetValueAsync<TValue>(string identifier, TValue value) => throw new NotSupportedException();
        public ValueTask SetValueAsync<TValue>(string identifier, TValue value, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
