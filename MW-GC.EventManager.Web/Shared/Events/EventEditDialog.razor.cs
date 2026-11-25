using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Shared.Events;

// The scope question of an edited Occurrence (#100, #204, stories 12 to 16 and 21): asked over this dialog before Update
// Event saves a change to an Event of a Series. A standalone Event, and an Occurrence with nothing changed, save as
// before. Cancel, Escape and overlay click drop the question and leave this dialog open with focus back in it
// (ScopeDialog tells focus.js); a confirmed scope runs through RunAsync, the save's busy guard.
public partial class EventEditDialog
{
    // The open scope question, if any.
    private ScopeRequest? _scope;

    // What the dialog holds now, as a change is read from it.
    private OccurrenceEdits Edits() =>
        new(_custName, BuildEventDate(), _custUniqueGames, _planner.Slots.Select(s => (s.GameId, s.ActivityId)).ToList());

    // A changed Occurrence not rolled yet (no Selections, ADR 0002) saves with none: the API takes an Occurrence with
    // none, and a series scope sends no slots. Every other save keeps the 1 to 5 rule, an unchanged Occurrence too.
    private bool Unrolled() =>
        _editingEvent is { SeriesId: not null } occurrence && _planner.Slots.Count == 0 && SeriesEdit.Compare(occurrence, Edits()).Any;

    // True when the save waits for the scope question. The Series id and the Event id are the edited row's own.
    private bool AskScope(EventEntity entity)
    {
        if (_scope is not null) return true;
        if (_editingEvent is not { SeriesId: { } seriesId } occurrence) return false;
        var edits = Edits();
        var change = SeriesEdit.Compare(occurrence, edits);
        if (!change.Any) return false;
        _scope = SeriesEdit.Question(change, occurrence.Name, occurrence.Date,
            () => SendAsync(() => EventSvc.UpdateAsync(entity)),
            () => RunAsync(() => UpdateSeriesAsync(occurrence, seriesId, edits, SeriesScope.Following)),
            () => RunAsync(() => UpdateSeriesAsync(occurrence, seriesId, edits, SeriesScope.All)));
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

    // Reads the Series and its eTag, then sends it back with only what the admin changed, under that eTag (a Series
    // changed meanwhile is the API's 409). The API's message, or null once saved.
    private async Task<string?> UpdateSeriesAsync(EventEntity occurrence, Guid seriesId, OccurrenceEdits edits, SeriesScope scope)
    {
        var read = await SeriesSvc.GetAsync(seriesId);
        if (read is not SeriesRead.Stored(var stored, var eTag)) return ((SeriesRead.Failed)read).Error;
        if (SeriesEdit.OtherDay(stored, occurrence, edits) is { } otherDay) return otherDay;
        using var response = await SeriesSvc.UpdateAsync(SeriesEdit.Body(stored, occurrence, edits, scope), eTag, scope, occurrence.Id);
        return await ApiResult.ErrorAsync(response);
    }
}
