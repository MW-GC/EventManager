using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Web.Services;

/// <summary>
/// Names an id from a loaded Library list, such as a Theme or Holiday tag (#183). The Games and Activities pages
/// and the Event editor's slots use this one lookup.
/// </summary>
public static class NameLookup
{
    /// <summary>
    /// The name of the item with <paramref name="id"/>, or the first 8 characters of the id when the list is not
    /// loaded yet, does not have the item, or the item has no name.
    /// </summary>
    public static string NameOrIdStart<T>(IEnumerable<T>? list, Guid id, Func<T, string?> name) where T : EntityBase =>
        list?.FirstOrDefault(x => x.Id == id) is { } match && name(match) is { } found
            ? found
            : id.ToString()[..8];
}
