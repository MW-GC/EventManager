namespace MW_GC.EventManager.Web.Shared;

/// <summary>
/// One answer a <c>ScopeDialog</c> offers: its visible label, a one-line description of what it does,
/// the confirm button's text once it is chosen (it names the scope), and what to run when it is confirmed.
/// </summary>
public sealed record ScopeOption(string Label, string Description, string ConfirmText, Func<Task> OnConfirm);

/// <summary>
/// A question a page shows in its <c>ScopeDialog</c> before a change to an Occurrence (#204): which of two or
/// three scopes it applies to. Nothing is chosen when it opens; Cancel, overlay click and Escape just drop it.
/// Compared by reference, so each question gets its own dialog instance.
/// </summary>
public sealed class ScopeRequest(string title, string message, string groupLabel, IReadOnlyList<ScopeOption> options, bool destructive)
{
    public string Title { get; } = title;
    public string Message { get; } = message;
    // The accessible name of the radio group.
    public string GroupLabel { get; } = groupLabel;
    public IReadOnlyList<ScopeOption> Options { get; } = options;
    public bool Destructive { get; } = destructive;

    /// <summary>
    /// Asked before an Occurrence is deleted from the Events list. Cancel scopes are two (#204): this one
    /// Occurrence (<paramref name="deleteOne"/>), or the whole Series from now on (<paramref name="cancelSeries"/>).
    /// </summary>
    public static ScopeRequest CancelOccurrence(string name, DateTimeOffset date, Func<Task> deleteOne, Func<Task> cancelSeries) =>
        new("Delete repeating event",
            $"{name} on {date.ToLocalTime():g} is one event of a series. Choose what to delete. This cannot be undone.",
            "What to delete",
            [
                new("This event only",
                    "It stays cancelled: the series will not make it again.",
                    "Delete this event", deleteOne),
                new("This and all future events",
                    "Stops the series and removes every future event of it that has not been played. Past and played events stay.",
                    "Cancel the series", cancelSeries)
            ],
            destructive: true);
}
