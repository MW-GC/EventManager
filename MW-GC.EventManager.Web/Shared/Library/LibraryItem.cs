using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Shared.Library;

/// <summary>
/// One item as a <see cref="LibraryPage{T}"/> row or card template sees it: the item, its row-action
/// state, and the page's own Edit and Delete for it. Edit asks nothing; Delete asks first and names the item.
/// The seven values stay one object on purpose: they are one item's view as a template sees it, and a split
/// would only make every template take two parameters.
/// </summary>
public sealed class LibraryItem<T>(
    T item, string name, bool busy, bool anyBusy,
    EventCallback<MouseEventArgs> edit, EventCallback<MouseEventArgs> delete,
    Func<Func<Task<string?>>, string, Task> run) where T : EntityBase
{
    public T Item => item;

    public string Name => name;

    /// <summary>This item's row action is running: its Edit is disabled.</summary>
    public bool Busy => busy;

    /// <summary>Some row's action is running: every Delete (and any other row action) is disabled.</summary>
    public bool AnyBusy => anyBusy;

    /// <summary>The data-focus-key of the Edit button, so focus returns to it when the dialog closes.</summary>
    public string EditKey => EditKeyOf(item.Id);

    /// <summary>The data-focus-key of the Delete button, so focus returns to it when the question closes.</summary>
    public string DeleteKey => DeleteKeyOf(item.Id);

    public EventCallback<MouseEventArgs> Edit => edit;

    public EventCallback<MouseEventArgs> Delete => delete;

    /// <summary>
    /// Runs another row action (such as Duplicate) under the page's busy guard: while it runs a second one
    /// sends nothing. <paramref name="write"/> returns null or an <see cref="ApiResult"/> message; a failure
    /// shows above the list as "<paramref name="failed"/> message", a success reloads the list.
    /// </summary>
    public Task RunAsync(Func<Task<string?>> write, string failed) => run(write, failed);

    // The one place the focus keys are built: the buttons carry them, and the page remembers them as a dialog opens.
    internal static string EditKeyOf(Guid id) => $"edit-{id}";

    internal static string DeleteKeyOf(Guid id) => $"delete-{id}";
}

/// <summary>Which of an item's own buttons a <c>LibraryItemButton</c> is.</summary>
public enum LibraryItemAction { Edit, Delete }

/// <summary>
/// The item in the open editor (a copy, so Cancel leaves the list as it was), and the item as listed when this
/// is an edit (null for an add). The editor's form lives here too, so a field can say whether it has a message
/// (<see cref="FieldAttributes"/>).
/// </summary>
public sealed class LibraryEdit<T>(T item, T? stored) where T : EntityBase
{
    public T Item => item;

    /// <summary>The item as the list holds it, before any change in the editor; null for an add.</summary>
    public T? Stored => stored;

    public bool IsEdit => stored is not null;

    /// <summary>The editor's form: its fields, rules and messages.</summary>
    internal EditContext Form { get; } = new(item);

    /// <summary>
    /// The attributes that tie a field to its <see cref="LibraryFieldMessage"/>: <c>aria-describedby</c> names the
    /// message, and <c>aria-invalid</c> is set while the field has one. <paramref name="field"/> is the same key the
    /// field's rule and message use.
    /// </summary>
    public IReadOnlyDictionary<string, object> FieldAttributes(LibraryField field)
    {
        var attributes = new Dictionary<string, object> { ["aria-describedby"] = field.MessageId };
        if (Form.GetValidationMessages(field.On(Item)).Any()) attributes["aria-invalid"] = "true";
        return attributes;
    }
}

/// <summary>
/// Another list a <see cref="LibraryPage{T}"/> loads with its own, into the same error bar and Retry, such
/// as the Activities the Games page counts. <see cref="ReloadAfterWrites"/>: also reload it after a save or delete.
/// </summary>
public sealed record LibraryLookup(Func<LoadErrors, Task> Load, bool ReloadAfterWrites = false);
