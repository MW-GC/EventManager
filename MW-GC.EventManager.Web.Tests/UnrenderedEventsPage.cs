using System.Reflection;
using Microsoft.AspNetCore.Components;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Pages;
using MW_GC.EventManager.Web.Shared.Events;

namespace MW_GC.EventManager.Web.Tests;

// The Events page and the two dialogs it was split into (#93), built without a renderer and wired to
// each other as the page's markup wires them, for tests that drive the page's handlers directly.
// A member is looked up on the create-and-edit dialog, then the details dialog, then the page, so a
// test names a handler or field as it did before the split. The library lists are the page's, and
// are handed to both dialogs, as the page's render does.
internal sealed class UnrenderedEventsPage
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags Any = Private | BindingFlags.Public;

    public Events Page { get; } = new();
    public EventEditDialog Edit { get; } = new();
    public EventDetailsDialog Details { get; } = new();

    private object[] Parts => [Edit, Details, Page];

    public UnrenderedEventsPage()
    {
        foreach (var part in Parts) SetProperty(part, "Focus", PageHarness.LooseFocus());
        SetProperty(Edit, nameof(EventEditDialog.Saved), new EventCallback(null, (Func<Task>)(() => (Task)Invoke(Page, "OnEventSaved")!)));
        SetProperty(Edit, nameof(EventEditDialog.PageError), new EventCallback<string?>(null, (Action<string?>)(m => Invoke(Page, "SetPageError", m))));
        SetProperty(Edit, nameof(EventEditDialog.ActionError), new EventCallback<string>(null, (Action<string>)(m => Invoke(Page, "SetActionError", m))));
        SetProperty(Details, nameof(EventDetailsDialog.RefreshEvents), new EventCallback(null, (Func<Task>)(() => (Task)Invoke(Page, "RefreshEvents")!)));
        SetProperty(Details, nameof(EventDetailsDialog.FindEvent), (Func<Guid, EventEntity?>)(id => (EventEntity?)Invoke(Page, "FindEvent", id)));
        SetProperty(Details, nameof(EventDetailsDialog.ActionError), new EventCallback<string>(null, (Action<string>)(m => Invoke(Page, "SetActionError", m))));
    }

    public object? Get(string name)
    {
        if (name is "_games" or "_activities") return Field(Page, name)!.GetValue(Page);
        var owner = Parts.First(p => Field(p, name) is not null);
        return Field(owner, name)!.GetValue(owner);
    }

    public void Set(string name, object? value)
    {
        if (name is "_games" or "_activities")
        {
            Field(Page, name)!.SetValue(Page, value);
            var parameter = name == "_games" ? "Games" : "Activities";
            SetProperty(Edit, parameter, value);
            SetProperty(Details, parameter, value);
            return;
        }
        var owner = Parts.First(p => Field(p, name) is not null);
        Field(owner, name)!.SetValue(owner, value);
    }

    public object? Call(string name, params object?[] args)
    {
        var owner = Parts.First(p => p.GetType().GetMethod(name, Private) is not null);
        return Invoke(owner, name, args);
    }

    // An injected service (EventSvc, ActivitySvc), set on every part that injects it.
    public void SetService(string name, object service)
    {
        foreach (var part in Parts)
            if (part.GetType().GetProperty(name, Private) is { } property) property.SetValue(part, service);
    }

    private static FieldInfo? Field(object part, string name) => part.GetType().GetField(name, Private);

    private static object? Invoke(object part, string name, params object?[] args) =>
        part.GetType().GetMethod(name, Private)!.Invoke(part, args);

    private static void SetProperty(object part, string name, object? value) =>
        part.GetType().GetProperty(name, Any)!.SetValue(part, value);
}
