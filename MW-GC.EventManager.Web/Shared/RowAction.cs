namespace MW_GC.EventManager.Web.Shared;

/// <summary>
/// The row action (Delete, Duplicate) a page is running. One runs at a time: meanwhile every
/// row's Delete and Duplicate are disabled and the busy row's other buttons too, and a click that
/// lands before the re-render sends nothing.
/// </summary>
public sealed class RowAction
{
    public Guid? BusyId { get; private set; }

    public bool Running => BusyId is not null;

    public bool IsBusy(Guid id) => BusyId == id;

    public async Task RunAsync(Guid id, Func<Task> action)
    {
        if (BusyId is not null) return;
        BusyId = id;
        try { await action(); }
        finally { BusyId = null; }
    }
}
