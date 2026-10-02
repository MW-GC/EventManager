namespace MW_GC.EventManager.Web.Services;

/// <summary>
/// Tracks which of a page's lists failed to load, so one list failing never hides the others
/// and the page can name exactly what is missing. A list keeps its previous contents on failure.
/// </summary>
public sealed class LoadErrors
{
    private readonly object _gate = new();
    // Lists in the order the page first loaded them, each with its current failure (null when loaded).
    private readonly List<(string Name, string? Error)> _lists = [];

    /// <summary>The message naming every failed list, or null when every list loaded.</summary>
    public string? Message
    {
        get { lock (_gate) return ApiResult.ListsFailed(_lists.ToList()); }
    }

    public bool Failed(string name)
    {
        lock (_gate) return _lists.Any(l => l.Name == name && l.Error is not null);
    }

    /// <summary>
    /// Loads one list. On success <paramref name="apply"/> receives the value and the list's error is
    /// cleared; on failure the error is recorded and <paramref name="apply"/> is not called. Never throws
    /// for an API failure.
    /// </summary>
    public async Task LoadAsync<T>(string name, Func<Task<T?>> load, Action<T?> apply)
    {
        // Register before awaiting so the message names lists in the order the page asked for them.
        lock (_gate)
        {
            if (!_lists.Any(l => l.Name == name)) _lists.Add((name, null));
        }
        var (value, error) = await ApiResult.LoadAsync(load);
        lock (_gate)
        {
            var index = _lists.FindIndex(l => l.Name == name);
            _lists[index] = (name, error);
        }
        if (error is null) apply(value);
    }
}
