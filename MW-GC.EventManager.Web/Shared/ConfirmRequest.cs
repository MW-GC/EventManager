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

    /// <summary>
    /// A Discard changes? question, always asked over an open form dialog. The page that shows a question
    /// sends focus back to the element it remembered (a row's Delete button) only when this is false.
    /// A Discard leaves focus to the form dialog instead: Keep editing hands it back into that dialog
    /// (ConfirmDialog, focus.js), and Discard closes the dialog, which returns focus to its own opener.
    /// A dialog that asks its own questions, such as a details dialog asking Delete Comment for its
    /// Comments panel, never sends focus back to its opener on an answer, whatever this says: the
    /// question gives focus back to the dialog that stays open.
    /// </summary>
    public bool OverDialog { get; private init; }

    /// <summary>Asks before a delete; Cancel leaves everything as it was and sends nothing.</summary>
    public static ConfirmRequest Delete(string title, string message, Func<Task> delete) =>
        new(title, message, "Delete", "Cancel", true, delete);

    /// <summary>Asked when a dialog with unsaved changes is dismissed by overlay click or Escape.</summary>
    public static ConfirmRequest Discard(Action discard) =>
        new("Discard changes?", "Your changes in this dialog have not been saved.", "Discard", "Keep editing", true,
            () => { discard(); return Task.CompletedTask; }) { OverDialog = true };

    /// <summary>
    /// The same question before an action that would drop unsaved changes without closing the dialog, such as
    /// Select Winner swapping the Comments panel; Discard runs <paramref name="discard"/> and waits for it.
    /// </summary>
    public static ConfirmRequest Discard(Func<Task> discard) =>
        new("Discard changes?", "Your changes in this dialog have not been saved.", "Discard", "Keep editing", true, discard) { OverDialog = true };

    /// <summary>
    /// What overlay click or Escape on a form dialog does; returns the page's next open question.
    /// While a save runs, nothing. With unsaved changes, ask. Otherwise close at once.
    /// The Cancel button does not come here: it closes at once, because the user chose it.
    /// A dialog that holds a select passes <paramref name="closeNow"/> for the clean case, so the
    /// close can wait for the select to finish with the same Escape (see DialogSession.CloseAfterKeyAsync);
    /// Discard still runs <paramref name="close"/>, since it is a click and no key on the select.
    /// </summary>
    public static ConfirmRequest? ForDismiss(ConfirmRequest? pending, bool saving, bool dirty, Action close, Action? closeNow = null)
    {
        // Every open dialog listens for Escape and the form dialog, opened first, hears it first;
        // so an Escape while the question is open lands here and answers it with Keep editing.
        if (pending is not null) return null;
        if (saving) return null;
        if (dirty) return Discard(close);
        (closeNow ?? close)();
        return null;
    }
}
