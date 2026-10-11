using Microsoft.JSInterop;

namespace MW_GC.EventManager.Web.Services;

/// <summary>
/// Puts keyboard focus back where it was when a dialog opened. A page calls <see cref="RememberAsync"/>
/// as a dialog opens (with the opener's <c>data-focus-key</c>) and <see cref="RestoreAsync"/> once
/// it closes. When the opener left the page meanwhile (its row re-rendered), focus goes to the element
/// with the same key, else to the page heading. Backed by <c>wwwroot/js/focus.js</c>; focus is a nicety,
/// so a missing or gone JS runtime never fails the page.
/// </summary>
public sealed class FocusReturn(IJSRuntime js)
{
    public Task RememberAsync(string? key = null) => InvokeAsync("emFocus.remember", key);

    public Task RestoreAsync() => InvokeAsync("emFocus.restore");

    /// <summary>
    /// A question (<c>ConfirmDialog</c>) is on the page: a form dialog behind it stops holding focus,
    /// and focus moves into the question. Called once the question has rendered.
    /// </summary>
    public Task QuestionAsync() => InvokeAsync("emFocus.question");

    /// <summary>
    /// The question is going away. If a form dialog is still open behind it (Keep editing), focus goes
    /// back to where it was in that dialog. With no dialog left, the page's own <see cref="RestoreAsync"/> decides.
    /// </summary>
    public Task AnsweredAsync() => InvokeAsync("emFocus.answered");

    private async Task InvokeAsync(string identifier, params object?[] args)
    {
        try
        {
            await js.InvokeVoidAsync(identifier, args);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException)
        {
        }
    }
}
