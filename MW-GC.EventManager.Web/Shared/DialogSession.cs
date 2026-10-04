using System.Text.Json;

namespace MW_GC.EventManager.Web.Shared;

/// <summary>
/// One open form dialog on a page. Every open and close starts a new session, so a save that
/// finishes after its dialog was closed (and maybe another one opened) can tell it no longer owns
/// the dialog. Opening also keeps the bound fields as text; a fresh copy that differs means the
/// dialog has unsaved changes, so no input needs its own dirty flag.
/// </summary>
public sealed class DialogSession
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private int _id;
    private Func<object>? _fields;
    private string? _snapshot;

    /// <summary>The token a save takes when it starts.</summary>
    public int Current => _id;

    /// <summary>True while the dialog the save started from is still the open one.</summary>
    public bool IsCurrent(int token) => token == _id;

    /// <summary>
    /// Starts a session and keeps <paramref name="fields"/> (usually an anonymous object of the bound
    /// values) as text. Without fields the session only tells late saves apart.
    /// </summary>
    public void Open(Func<object>? fields = null)
    {
        _id++;
        _fields = fields;
        _snapshot = fields is null ? null : Serialize(fields());
    }

    public void Close()
    {
        _id++;
        _fields = null;
        _snapshot = null;
    }

    /// <summary>The bound fields differ from what they were when the dialog opened.</summary>
    public bool IsDirty => _fields is not null && Serialize(_fields()) != _snapshot;

    private static string Serialize(object fields) => JsonSerializer.Serialize(fields, Options);
}
