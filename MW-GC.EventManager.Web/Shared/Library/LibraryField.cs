using Microsoft.AspNetCore.Components.Forms;

namespace MW_GC.EventManager.Web.Shared.Library;

/// <summary>
/// One field of a Library item's editor, by the item's property name (such as nameof(GameEntity.Website)). The
/// field's rule (<see cref="LibraryFieldError"/>), its messages (<see cref="LibraryFieldMessage"/>) and its
/// attributes (<see cref="LibraryEdit{T}.FieldAttributes"/>) all take this one key, so a page declares a field once
/// and the three cannot disagree on its spelling.
/// </summary>
public sealed record LibraryField(string Property)
{
    /// <summary>The name every Library item has, which the shared page edits and checks itself.</summary>
    public static LibraryField ItemName { get; } = new("Name");

    /// <summary>
    /// The id of the element that holds this field's messages. A page opens one editor at a time (the Add or Edit
    /// dialog), so the id is unique on the page while that editor is open; a second editor open at once would need
    /// its own prefix.
    /// </summary>
    public string MessageId => $"library-field-{Property}-message";

    /// <summary>This field on the item in the form, as the form's rules and messages name it.</summary>
    public FieldIdentifier On(object item) => new(item, Property);
}

/// <summary>
/// The item's name as a <see cref="LibraryPage{TItem}"/> reads and edits it: shown, searched, sorted, edited and
/// checked by the name rule. A name stored as null reads as empty (#196.3), so a search never throws on it.
/// </summary>
public sealed class LibraryName<T>(Func<T, string?> read, Action<T, string> write)
{
    public string Of(T item) => read(item) ?? string.Empty;

    /// <summary>Writes the name typed in the editor onto the editor's copy of the item.</summary>
    public void Set(T item, string name) => write(item, name);
}
