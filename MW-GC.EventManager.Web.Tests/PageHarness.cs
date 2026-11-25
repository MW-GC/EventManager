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
        // The four Library clients as the app registers them (AppServices), over the stub's HttpClient.
        _context.Services.AddSingleton(http);
        _context.Services.AddLibraryClients();
        _context.Services.AddSingleton(new ActivityService(http));
        _context.Services.AddSingleton(new EventService(http));
        _context.Services.AddSingleton(new SeriesService(http));
        _context.Services.AddSingleton(new EventMetricsClient(http));
        _context.Services.AddSingleton(new CommentClient(http));
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

// The Comments panel inside a page's details dialog (Activities or Events), as a user sees it (#131, #132).
// Shared by the tests of both dialogs; import with `using static`. What any page does (a row's button, Dismiss)
// is in PageActions (#200.9), not here.
internal static class CommentsPanelView
{
    // One list item per Comment, in the order shown.
    public static List<IElement> Items<T>(IRenderedComponent<T> page) where T : IComponent =>
        page.FormDialog().QuerySelectorAll("li.comment").ToList();

    // Each Comment's text, or "(editing)" for the one open in its edit box.
    public static List<string> Texts<T>(IRenderedComponent<T> page) where T : IComponent =>
        Items(page).Select(i => i.QuerySelector(".comment-text")?.TextContent ?? "(editing)").ToList();

    public static IElement? NewBox<T>(IRenderedComponent<T> page) where T : IComponent =>
        page.FormDialog().QuerySelector("fluent-text-area[aria-label='New Comment']");

    public static IElement? EditBox<T>(IRenderedComponent<T> page) where T : IComponent =>
        page.FormDialog().QuerySelector("fluent-text-area[aria-label^='Edit Comment from']");

    // The dialog's one button with this accessible name, such as "Delete Comment from 2025-03-14 12:00".
    public static IElement ButtonNamed<T>(IRenderedComponent<T> page, string label) where T : IComponent =>
        page.FormDialog().QuerySelectorAll("fluent-button").Single(x => x.GetAttribute("aria-label") == label);

    public static Task TypeNew<T>(IRenderedComponent<T> page, string text) where T : IComponent =>
        NewBox(page)!.InputAsync(new ChangeEventArgs { Value = text });

    // A click whose request finishes at once; waits for the render after it.
    public static async Task Click<T>(IElement button, IRenderedComponent<T> page) where T : IComponent
    {
        await button.ClickAsync(new());
        page.WaitForAssertion(() => Assert.IsFalse(page.FormDialog().Button("Close").IsBusy()));
    }

    // A Comment's added time: a local wall-clock time, sent in UTC as the API sends it. The panel shows local
    // time, so a test can write the shown date and time as a literal in any time zone.
    public static DateTimeOffset AddedAtLocal(int year, int month, int day, int hour, int minute) =>
        new DateTimeOffset(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local)).ToUniversalTime();
}
