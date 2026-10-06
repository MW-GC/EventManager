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

    /// <summary>
    /// Runs <paramref name="close"/> once the key that dismissed the dialog is done with it, if this
    /// session is still the open one then. Escape on a Fluent UI select reaches the select as well:
    /// Fluent UI 4.14.4 (ListComponentBase.OnKeydownHandlerAsync) waits one millisecond and then calls
    /// the select's JS module. Closing at once removes the select and disposes that module first, so
    /// that call throws and the page falls into the error boundary (#105).
    /// The select starts its wait just before or just after this dismiss, in the same key event, and
    /// two timers that come due in the same tick may run in either order. Each wait here starts when
    /// the one before it ended, so by the time the third starts the select's wait is over, and the
    /// close runs after the select made its call.
    /// </summary>
    public async Task CloseAfterKeyAsync(Action close)
    {
        var token = _id;
        for (var wait = 0; wait < 3; wait++)
            await Task.Delay(1);
        if (IsCurrent(token)) close();
    }

    /// <summary>The bound fields differ from what they were when the dialog opened.</summary>
    public bool IsDirty => _fields is not null && Serialize(_fields()) != _snapshot;

    private static string Serialize(object fields) => JsonSerializer.Serialize(fields, Options);
}
