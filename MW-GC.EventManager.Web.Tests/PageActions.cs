using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;

namespace MW_GC.EventManager.Web.Tests;

// What a user does on a rendered page, shared by the page tests: one copy of each helper (#195 item 8, #200
// item 9). Elements are looked up again on every call, because a re-render replaces them.
internal static class PageActions
{
    // A Selection of an Activity of a Game, as an Event stores it.
    public static Selection Pick(GameEntity game, ActivityEntity activity) => new()
    {
        Game = new Game { Id = game.Id, Name = game.Name },
        Activity = new Activity { Id = activity.Id, GameId = activity.GameId, Name = activity.Name }
    };

    // The page's row (outside any dialog) that names the item, and its button with this title, such as Details.
    public static IElement RowButton<T>(IRenderedComponent<T> page, string item, string title) where T : IComponent =>
        page.FindAll("tbody tr.fluent-data-grid-row")
            .Where(r => r.Closest("fluent-dialog") is null)
            .Single(r => r.TextContent.Contains(item))
            .QuerySelector($"fluent-button[title={title}]")!;

    // The card in the page's form or details dialog that shows this text as a label, such as one Selection's
    // Activity name in the Event details or "Event metrics".
    public static IElement Card<T>(IRenderedComponent<T> page, string label) where T : IComponent =>
        page.FormDialog().QuerySelectorAll("fluent-card")
            .Single(c => c.QuerySelectorAll(".fluent-typography").Any(l => l.TextContent.Trim() == label));

    // Overlay click or Escape on the page's form or details dialog: the "dismiss" event Fluent UI raises on
    // <fluent-dialog>, which reaches the page as "ondialogdismiss".
    public static Task Dismiss<T>(IRenderedComponent<T> page) where T : IComponent
    {
        var dialog = page.FormDialog();
        return dialog.TriggerEventAsync("ondialogdismiss", new DialogEventArgs { Id = dialog.Id, Reason = "dismiss" });
    }

    // The click handlers of the page's buttons with this text (or, for an icon button, this title), in page order,
    // as the page wired them. Taken while a button is enabled, a handler lets a test run a click that lands before
    // the re-render that disables the button: the click then reaches the handler's own guard. Run one with
    // page.InvokeAsync(() => handler.InvokeAsync(new())).
    public static List<EventCallback<MouseEventArgs>> ClickHandlers(IRenderedComponent<IComponent> page, string text) =>
        page.FindComponents<FluentButton>()
            .Where(b => b.Find("fluent-button") is var e && (e.TextContent.Trim() == text || e.GetAttribute("title") == text))
            .Select(b => b.Instance.OnClick).ToList();

    // The click handler of the page's one button with this text.
    public static EventCallback<MouseEventArgs> ClickHandler(IRenderedComponent<IComponent> page, string text) =>
        Assert.ContainsSingle(ClickHandlers(page, text));

    // Text as a screen reader reads it: every run of white space is one space.
    public static string Normalize(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
