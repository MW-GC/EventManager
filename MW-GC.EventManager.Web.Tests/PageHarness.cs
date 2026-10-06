using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Tests;

// Renders a real page with bUnit: Fluent UI services, loose JS interop (Fluent components call
// into JS that does not exist here), and every page service backed by the stub API's HttpClient.
internal sealed class PageHarness : IDisposable
{
    private readonly BunitContext _context = new();

    public PageHarness(StubApiHandler api)
    {
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
        _context.Services.AddFluentUIComponents();
        var http = api.Client();
        _context.Services.AddSingleton(new GameService(http));
        _context.Services.AddSingleton(new ActivityService(http));
        _context.Services.AddSingleton(new EventService(http));
        _context.Services.AddSingleton(new ThemeService(http));
        _context.Services.AddSingleton(new HolidayService(http));
        _context.Services.AddScoped<FocusReturn>();
    }

    // A FocusReturn for a page built without a renderer (new Events()): its JS calls go nowhere.
    public static FocusReturn LooseFocus() => new(new BunitJSInterop { Mode = JSRuntimeMode.Loose }.JSRuntime);

    public IRenderedComponent<TPage> Render<TPage>() where TPage : IComponent => _context.Render<TPage>();

    // A shared component on its own, with parameters.
    public IRenderedComponent<TComponent> Render<TComponent>(Action<ComponentParameterCollectionBuilder<TComponent>> parameters)
        where TComponent : IComponent => _context.Render(parameters);

    // bUnit's JS interop, so a test can answer one JS call its own way.
    public BunitJSInterop JSInterop => _context.JSInterop;

    // Completes when a component throws and nothing catches it: in the app, the error boundary.
    public Task<Exception> UnhandledException => _context.Renderer.UnhandledException;

    public void Dispose() => _context.Dispose();
}

// What a user sees on a rendered page. Elements are looked up again on every call, because a
// re-render replaces them.
internal static class Rendered
{
    // The page's list rows, outside any dialog.
    public static List<string> Rows<T>(this IRenderedComponent<T> cut) where T : IComponent =>
        cut.FindAll("tbody tr.fluent-data-grid-row").Where(r => r.Closest("fluent-dialog") is null)
            .Select(r => r.TextContent).ToList();

    // Waits for the first load to finish showing a list.
    public static void WaitForRows<T>(this IRenderedComponent<T> cut, int count) where T : IComponent =>
        cut.WaitForAssertion(() => Assert.HasCount(count, cut.Rows()));

    // Waits for the first load to finish showing a message instead of a list.
    public static void WaitForPageAlert<T>(this IRenderedComponent<T> cut) where T : IComponent =>
        cut.WaitForAssertion(() => Assert.IsNotEmpty(cut.PageAlerts()));

    // Messages shown on the page itself (load failures, failed deletes), not inside a dialog.
    public static List<string> PageAlerts<T>(this IRenderedComponent<T> cut) where T : IComponent =>
        cut.FindAll("[role=alert]").Where(a => a.Closest("fluent-dialog") is null).Select(AlertText).ToList();

    public static bool HasDialog<T>(this IRenderedComponent<T> cut) where T : IComponent =>
        cut.FindAll("fluent-dialog").Count > 0;

    public static IElement Dialog<T>(this IRenderedComponent<T> cut) where T : IComponent => cut.Find("fluent-dialog");

    // The page's form or details dialog, never the confirmation shown over it.
    public static IElement FormDialog<T>(this IRenderedComponent<T> cut) where T : IComponent =>
        cut.FindAll("fluent-dialog").Single(d => !d.ClassList.Contains("confirm-dialog"));

    public static bool HasFormDialog<T>(this IRenderedComponent<T> cut) where T : IComponent =>
        cut.FindAll("fluent-dialog").Any(d => !d.ClassList.Contains("confirm-dialog"));

    // The ConfirmDialog question (a delete or a discard) shown over the page.
    public static IElement Confirmation<T>(this IRenderedComponent<T> cut) where T : IComponent => cut.Find("fluent-dialog.confirm-dialog");

    public static bool HasConfirmation<T>(this IRenderedComponent<T> cut) where T : IComponent =>
        cut.FindAll("fluent-dialog.confirm-dialog").Count > 0;

    // Answers the open confirmation with its confirm button, such as Delete.
    public static Task ConfirmAsync<T>(this IRenderedComponent<T> cut, string text = "Delete") where T : IComponent =>
        cut.Confirmation().Button(text).ClickAsync(new());

    public static string DialogTitle<T>(this IRenderedComponent<T> cut) where T : IComponent =>
        cut.Find("fluent-dialog .fluent-dialog-body > .fluent-typography").TextContent.Trim();

    // Messages shown under an element, such as an open dialog.
    public static List<string> Alerts(this IElement root) =>
        root.QuerySelectorAll("[role=alert]").Select(AlertText).ToList();

    public static IElement Button<T>(this IRenderedComponent<T> cut, string text) where T : IComponent =>
        cut.FindAll("fluent-button").Single(b => b.TextContent.Trim() == text);

    public static IElement Button(this IElement root, string text) =>
        root.QuerySelectorAll("fluent-button").Single(b => b.TextContent.Trim() == text);

    // A button that is disabled or still shows its saving spinner.
    public static bool IsBusy(this IElement button) =>
        button.HasAttribute("disabled") || button.QuerySelector("fluent-progress-ring") is not null;

    // An ApiErrorBar shows its message in a span next to its buttons; other alerts are plain text.
    private static string AlertText(IElement alert) =>
        (alert.QuerySelector(".fluent-messagebar-message span") ?? alert).TextContent.Trim();
}
