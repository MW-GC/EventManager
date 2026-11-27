using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Shared.Events;

// The scope question of an edited Occurrence (#100, #204, stories 12 to 16, 21 and 22): asked over this dialog before
// Update Event saves a change to an Event of a Series. A standalone Event saves as before; an Occurrence with nothing
// changed sends nothing (EventEditDialog.Save.cs). Cancel, Escape and overlay click drop the question and leave this
// dialog open with focus back in it (ScopeDialog tells focus.js); a confirmed scope runs through RunAsync, the save's
// busy guard.
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

    // Called once the edit dialog has opened (its unsaved-changes baseline taken): the first Randomize All aims for the
    // dialog's own slot count again, and an Occurrence waits for its Series read to start from its slot template.
    private void StartFromSeriesOf(EventEntity e)
    {
        _planner.StartingSlotCount = SlotPlanner.DefaultSlotCount;
        if (_seriesRead is { } read) _ = SeedWhenReadAsync(e, read);
    }

    // #204 story 11, #237 S5: an Occurrence not rolled yet (no Selections) starts from its Series' slot template: the
    // slot count the first Randomize All aims for and the Filters, typed into the same boxes the admin types into.
    // Seeded only into the dialog that started the read (a dialog closed or opened again since is another session),
    // and only while the admin has changed nothing in it: a change made before the read finished is kept, and nothing
    // is seeded (the save's busy guard, the scope and the Discard questions all follow a change, so none is open then).
    // A failed read seeds nothing and says nothing: the dialog rolls with its own defaults, as before. The seeded values
    // are the dialog's new baseline, so closing it untouched asks nothing. The dialog draws itself again once the read
    // is in, whatever it seeded.
    private async Task SeedWhenReadAsync(EventEntity occurrence, Task<SeriesRead> read)
    {
        var token = _customize.Current;
        var opened = await read;
        await InvokeAsync(() =>
        {
            if (_customize.IsCurrent(token) && occurrence.Selections is not { Count: > 0 } && !_customize.IsDirty
                && opened is SeriesRead.Stored(var series, _))
                Seed(series);
            StateHasChanged();
        });
    }

    private void Seed(SeriesEntity series)
    {
        var filters = series.Filters ?? new();
        _planner.StartingSlotCount = series.SlotCount;
        _custThemedOnly = filters.ThemedOnly;
        _custGameIds = Known(filters.GameIds, Games);
        _custThemeIds = Known(filters.ThemeIds, Themes);
        _custHolidayIds = Known(filters.HolidayIds, Holidays);
        _avoidRecent.Type(BoxText(filters.AvoidRecent));
        _playersDuration.Type(PlayersDurationFilters.PlayersTonight, BoxText(filters.PlayersTonight));
        _playersDuration.Type(PlayersDurationFilters.LongestDuration, BoxText(filters.LongestDuration));
        SyncFilter();
        _customize.Open(CustomizeFields);
    }

    // #246: only the Series' ids the library the page holds still lists. A Game, Theme or Holiday deleted since the
    // Series was saved has no checkbox in the Filters panel, so a seeded id of it could never be cleared; it is dropped,
    // and a group whose ids are all gone seeds that Filter off. The Series itself is not changed.
    private static List<Guid> Known(IEnumerable<Guid>? ids, IEnumerable<EntityBase>? library)
    {
        var listed = library?.Select(entry => entry.Id).ToHashSet() ?? [];
        return ids?.Where(listed.Contains).ToList() ?? [];
    }

    // A stored number as its box shows it; null (off) is an empty box.
    private static string BoxText(int? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    // What the dialog holds now, as a change is read from it.
    private OccurrenceEdits Edits() =>
        new(_custName, BuildEventDate(), _custUniqueGames, _planner.Slots.Select(s => (s.GameId, s.ActivityId)).ToList());

    // A changed Occurrence not rolled yet (no Selections, ADR 0002) saves with none: the API takes an Occurrence with
    // none, and a series scope sends no slots. Every other save keeps the 1 to 5 rule (an unchanged Occurrence sends
    // nothing and closes before it, #237 S1).
    private bool Unrolled() =>
        _editingEvent is { SeriesId: not null } occurrence && _planner.Slots.Count == 0 && SeriesEdit.Compare(occurrence, Edits()).Any;

    // True when the save waits for the scope question. The Series id, the Event id and the Series read are the edited
    // row's own. An Occurrence a Series change skips (#237 S2) asks nothing: only This event only would reach it. A
    // question with the series scopes in it waits for the Series read, the only thing that tells the first Occurrence
    // (#237 S3); a failed read offers both, and a series scope then says the read failed. A save pressed again while it
    // waits does nothing, and a dialog closed or opened again meanwhile is left alone.
    private async Task<bool> AskScopeAsync(EventEntity entity)
    {
        if (_scope is not null || _awaitingSeries) return true;
        if (_editingEvent is not { SeriesId: not null } occurrence || SeriesSkipsThis) return false;
        var edits = Edits();
        var change = SeriesEdit.Compare(occurrence, edits);
        if (!change.Any) return false;
        // ShowEditEvent always started the read; without one a series scope fails closed, as a failed read does.
        var read = _seriesRead ?? Task.FromResult<SeriesRead>(new SeriesRead.Failed(ApiResult.NotFoundMessage));
        var first = false;
        if (change.SeriesWide)
        {
            var token = _customize.Current;
            _awaitingSeries = true;
            try { first = await read is SeriesRead.Stored(var series, _) && SeriesEdit.IsFirst(occurrence, series); }
            finally { _awaitingSeries = false; }
            // Closed, opened again, or a Discard question opened by Escape while the read ran: no question now.
            if (!_customize.IsCurrent(token) || _confirm is not null) return true;
        }
        _scope = SeriesEdit.Question(change, occurrence.Name, occurrence.Date,
            () => SendAsync(() => EventSvc.UpdateAsync(entity, occurrence.ETag)),
            first ? null : () => RunAsync(() => UpdateSeriesAsync(occurrence, read, edits, SeriesScope.Following)),
            () => RunAsync(() => UpdateSeriesAsync(occurrence, read, edits, SeriesScope.All)));
        return true;
    }

    // Set while a save waits for the Series read before its question.
    private bool _awaitingSeries;

    // #237 S2: the edited Event is an Occurrence a Series change leaves as it is (SeriesEdit.SeriesSkips), so the dialog
    // says so and its save asks no scope.
    private bool SeriesSkipsThis => _editingEvent is { SeriesId: not null } e && SeriesEdit.SeriesSkips(e, Clock.GetUtcNow());

    // The app registers no TimeProvider, so the system clock unless one is registered (a test pins one).
    [Inject] private IServiceProvider Services { get; set; } = default!;
    private TimeProvider Clock => Services.GetService<TimeProvider>() ?? TimeProvider.System;

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
