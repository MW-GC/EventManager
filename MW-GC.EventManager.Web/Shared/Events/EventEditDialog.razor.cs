using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Shared.Events;

// The scope question of an edited Occurrence (#100, #204, stories 12 to 16, 21 and 22): asked over this dialog before
// Update Event saves a change to an Event of a Series. A standalone Event, and an Occurrence with nothing changed, save
// as before. Cancel, Escape and overlay click drop the question and leave this dialog open with focus back in it
// (ScopeDialog tells focus.js); a confirmed scope runs through RunAsync, the save's busy guard.
public partial class EventEditDialog
{
    // The open scope question, if any.
    private ScopeRequest? _scope;

    // The edited Occurrence's Series as it was when this dialog opened, with its eTag (story 22). A series scope sends
    // it back under that eTag, so a Series another admin saved after the dialog opened is the API's 409, never a silent
    // overwrite. Only a series scope waits for it: This event only and an unchanged save never read it.
    private Task<SeriesRead>? _seriesRead;

    // Called each time the edit dialog opens: starts the read of an Occurrence's Series, or drops the last one.
    private void ReadSeriesOf(EventEntity? e) => _seriesRead = e?.SeriesId is { } seriesId ? SeriesSvc.GetAsync(seriesId) : null;

    // What the dialog holds now, as a change is read from it.
    private OccurrenceEdits Edits() =>
        new(_custName, BuildEventDate(), _custUniqueGames, _planner.Slots.Select(s => (s.GameId, s.ActivityId)).ToList());

    // A changed Occurrence not rolled yet (no Selections, ADR 0002) saves with none: the API takes an Occurrence with
    // none, and a series scope sends no slots. Every other save keeps the 1 to 5 rule, an unchanged Occurrence too.
    private bool Unrolled() =>
        _editingEvent is { SeriesId: not null } occurrence && _planner.Slots.Count == 0 && SeriesEdit.Compare(occurrence, Edits()).Any;

    // True when the save waits for the scope question. The Series id, the Event id and the Series read are the edited
    // row's own.
    private bool AskScope(EventEntity entity)
    {
        if (_scope is not null) return true;
        if (_editingEvent is not { SeriesId: not null } occurrence) return false;
        var edits = Edits();
        var change = SeriesEdit.Compare(occurrence, edits);
        if (!change.Any) return false;
        // ShowEditEvent always started the read; without one a series scope fails closed, as a failed read does.
        var read = _seriesRead ?? Task.FromResult<SeriesRead>(new SeriesRead.Failed(ApiResult.NotFoundMessage));
        _scope = SeriesEdit.Question(change, occurrence.Name, occurrence.Date,
            () => SendAsync(() => EventSvc.UpdateAsync(entity)),
            () => RunAsync(() => UpdateSeriesAsync(occurrence, read, edits, SeriesScope.Following)),
            () => RunAsync(() => UpdateSeriesAsync(occurrence, read, edits, SeriesScope.All)));
        return true;
    }

    // Escape and overlay click on this dialog while the question is open answer it with Cancel.
    private bool DropScope()
    {
        if (_scope is null) return false;
        _scope = null;
        return true;
    }

    private async Task ScopeConfirmed(ScopeRequest scope, ScopeOption option)
    {
        if (_scope != scope) return;
        _scope = null;
        await option.OnConfirm();
    }

    private void ScopeCancelled(ScopeRequest scope)
    {
        if (_scope == scope) _scope = null;
    }

    // Sends the Series read when this dialog opened back with only what the admin changed, under that read's eTag (a
    // Series changed since is the API's 409). A failed read is not repeated here: a read now would take a version newer
    // than the one the admin opened. A read without an eTag sends nothing. The API's message, or null once saved.
    private async Task<string?> UpdateSeriesAsync(EventEntity occurrence, Task<SeriesRead> read, OccurrenceEdits edits, SeriesScope scope)
    {
        var opened = await read;
        if (opened is SeriesRead.Failed(var error)) return $"{error} {SeriesEdit.ReadAgainMessage}";
        var (stored, eTag) = (SeriesRead.Stored)opened;
        if (string.IsNullOrEmpty(eTag)) return SeriesEdit.NoVersionMessage;
        if (SeriesEdit.OtherDay(stored, occurrence, edits) is { } otherDay) return otherDay;
        using var response = await SeriesSvc.UpdateAsync(SeriesEdit.Body(stored, occurrence, edits, scope), eTag, scope, occurrence.Id);
        return await ApiResult.ErrorAsync(response);
    }
}
