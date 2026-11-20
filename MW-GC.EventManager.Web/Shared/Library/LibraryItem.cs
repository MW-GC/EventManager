using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Shared.Library;

/// <summary>
/// One item as a <see cref="LibraryPage{T}"/> row or card template sees it: the item, its row-action
/// state, and the page's own Edit and Delete for it. Edit asks nothing; Delete asks first and names the item.
/// </summary>
public sealed class LibraryItem<T>(
    T item, string name, bool busy, bool running,
    EventCallback<MouseEventArgs> edit, EventCallback<MouseEventArgs> delete,
    Func<Func<Task<string?>>, string, Task> run) where T : EntityBase
{
    public T Item => item;

    public string Name => name;

    /// <summary>This item's row action is running: its Edit is disabled.</summary>
    public bool Busy => busy;

    /// <summary>Some row action is running: every Delete (and any other row action) is disabled.</summary>
    public bool Running => running;

    /// <summary>The data-focus-key of the Edit button, so focus returns to it when the dialog closes.</summary>
    public string EditKey => $"edit-{item.Id}";

    /// <summary>The data-focus-key of the Delete button, so focus returns to it when the question closes.</summary>
    public string DeleteKey => $"delete-{item.Id}";

    public EventCallback<MouseEventArgs> Edit => edit;

    public EventCallback<MouseEventArgs> Delete => delete;

    /// <summary>
    /// Runs another row action (such as Duplicate) under the page's busy guard: while it runs a second one
    /// sends nothing. <paramref name="write"/> returns null or an <see cref="ApiResult"/> message; a failure
    /// shows above the list as "<paramref name="failed"/> message", a success reloads the list.
    /// </summary>
    public Task RunAsync(Func<Task<string?>> write, string failed) => run(write, failed);
}

/// <summary>The item in the open editor, and whether it is an edit (else an add).</summary>
public sealed record LibraryEdit<T>(T Item, bool IsEdit) where T : EntityBase;

/// <summary>
/// Another list a <see cref="LibraryPage{T}"/> loads with its own, into the same error bar and Retry, such
/// as the Activities the Games page counts. <see cref="ReloadAfterWrites"/>: also reload it after a save or delete.
/// </summary>
public sealed record LibraryLookup(Func<LoadErrors, Task> Load, bool ReloadAfterWrites = false);
