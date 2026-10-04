namespace MW_GC.EventManager.Web.Shared;

/// <summary>
/// A question a page shows in its <c>ConfirmDialog</c>, with what to run when the user confirms.
/// A page keeps at most one open; Cancel, overlay click and Escape just drop it. Compared by
/// reference, so each question gets its own dialog instance.
/// </summary>
public sealed class ConfirmRequest(string title, string message, string confirmText, string cancelText, bool destructive, Func<Task> onConfirm)
{
    public string Title { get; } = title;
    public string Message { get; } = message;
    public string ConfirmText { get; } = confirmText;
    public string CancelText { get; } = cancelText;
    public bool Destructive { get; } = destructive;
    public Func<Task> OnConfirm { get; } = onConfirm;

    /// <summary>Asks before a delete; Cancel leaves everything as it was and sends nothing.</summary>
    public static ConfirmRequest Delete(string title, string message, Func<Task> delete) =>
        new(title, message, "Delete", "Cancel", true, delete);

    /// <summary>Asked when a dialog with unsaved changes is dismissed by overlay click or Escape.</summary>
    public static ConfirmRequest Discard(Action discard) =>
        new("Discard changes?", "Your changes in this dialog have not been saved.", "Discard", "Keep editing", true,
            () => { discard(); return Task.CompletedTask; });

    /// <summary>
    /// What overlay click or Escape on a form dialog does; returns the page's next open question.
    /// While a save runs, nothing. With unsaved changes, ask. Otherwise close at once.
    /// The Cancel button does not come here: it closes at once, because the user chose it.
    /// </summary>
    public static ConfirmRequest? ForDismiss(ConfirmRequest? pending, bool saving, bool dirty, Action close)
    {
        // Every open dialog listens for Escape and the form dialog, opened first, hears it first;
        // so an Escape while the question is open lands here and answers it with Keep editing.
        if (pending is not null) return null;
        if (saving) return null;
        if (dirty) return Discard(close);
        close();
        return null;
    }
}
