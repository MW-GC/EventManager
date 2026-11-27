using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Shared.Events;

// The save of the create-and-edit dialog: Create Event or Update Event builds the Event (or, with Repeat on, the
// Series) from what the dialog holds, checks it, and sends it through RunAsync, the save's busy guard, which closes the
// dialog and tells the page once it is saved. An Occurrence's change asks its scope question first
// (EventEditDialog.razor.cs).
public partial class EventEditDialog
{
    // What a slot saves: its snapshot while unchanged, otherwise rebuilt from the library.
    // Null when a changed slot names a Game or Activity the library does not offer.
    private Selection? BuildSelection(SlotSelection s)
    {
        if (SlotPanel.Unchanged(s) is { } stored) return stored;
        var game = Games?.FirstOrDefault(g => g.Id == s.GameId);
        var act = Activities?.FirstOrDefault(a => a.Id == s.ActivityId && a.GameId == s.GameId);
        if (game is null || act is null) return null;
        return SelectionSnapshot.From(game, act);
    }

    // The slot held the Event's Winner and its Activity has been changed. One slot always wins.
    private bool ReplacesWinner(SlotSelection s) =>
        _editingEvent?.WinnerActivityId is { } winner && _planner.Slots.Count > 1 &&
        s.Stored?.Activity.Id == winner && s.ActivityId != winner;

    // Combines the selected date and time, then resolves the user's local UTC offset so the
    // stored DateTimeOffset represents the correct instant for downstream consumers such as
    // the planned Discord scheduled-event integration.
    private DateTimeOffset BuildEventDate()
    {
        if (!_custDate.HasValue) return DateTimeOffset.Now;
        var wallClock = _custDate.Value.Date + (_custTime?.TimeOfDay ?? TimeSpan.Zero);
        return new DateTimeOffset(DateTime.SpecifyKind(wallClock, DateTimeKind.Local));
    }

    private async Task SaveCustomizedEvent()
    {
        if (_generating || string.IsNullOrWhiteSpace(_custName)) return;
        // #237 S1: an Occurrence with nothing changed since the dialog opened (the baseline the Discard question uses,
        // the Filters included) sends nothing, since the API would mark it Modified; the dialog closes as Cancel does.
        if (_editingEvent is { SeriesId: not null } && !_customize.IsDirty)
        {
            CancelCustomize();
            return;
        }
        _customizeError = null;
        var slots = _planner.Slots;
        if (slots.Count is < 1 or > EventEntity.MaximumSelections && !Unrolled())
        {
            _customizeError = "Select 1–5 activities before saving. Adjust the filters or add an activity slot.";
            return;
        }
        // Repeat on: one Series, no Event. Its Occurrences start with no Selections, so the roll is not saved.
        if (Repeating)
        {
            var series = BuildSeries();
            await SendAsync(() => SeriesSvc.CreateAsync(series, _createKey));
            return;
        }
        // Unchanged slots save their stored snapshot; a changed slot must be a library choice.
        var built = slots.Select(BuildSelection).ToList();
        if (slots.Any(s => s.GameId == Guid.Empty || s.ActivityId == Guid.Empty) || built.Any(s => s is null))
        {
            _customizeError = "Choose a valid game and activity for every slot before saving.";
            return;
        }
        var entity = new EventEntity
        {
            Id = _editingEvent?.Id ?? Guid.Empty,
            Name = _custName,
            Date = BuildEventDate(),
            Selections = built.OfType<Selection>().ToList(),
            UniqueGamesOnly = _custUniqueGames,
            // Replacing the Winner's slot clears the Winner, as its notice says.
            WinnerActivityId = slots.Any(ReplacesWinner) ? null : _editingEvent?.WinnerActivityId
        };
        if (!Unrolled() && entity.ValidateSelections() is not null)
        {
            _customizeError = "Activities must be distinct, and games must be distinct when Unique games only is enabled.";
            return;
        }
        entity.NormalizeWinner();
        // An Occurrence with a change asks which events take it first (#204).
        if (await AskScopeAsync(entity)) return;
        // An edit goes under the eTag of the Event the dialog opened (story 22): a stale one is the API's 409.
        await SendAsync(() => _editingEvent is not null ? EventSvc.UpdateAsync(entity, _editingEvent.ETag) : EventSvc.SaveAsync(entity, _createKey));
    }

    // The Series a create with Repeat on sends (#204): the start exactly as the Event's, its offset kept (the Series'
    // calendar arithmetic runs at it), the slot count and the Filters the dialog holds. Avoid recent, players tonight
    // and longest duration (#232) go as the Filter's own setting, read as the Filters panel reads its boxes: null while
    // a box is empty or holds text it does not take, so a box that filters nothing is sent as off.
    private SeriesEntity BuildSeries()
    {
        var boxes = _playersDuration.ApplyTo(SlotFilter.Default);
        return new()
        {
            Id = _createKey,
            Name = _custName,
            Schedule = _repeat.ScheduleFor(BuildEventDate()),
            SlotCount = _planner.Slots.Count,
            UniqueGamesOnly = _custUniqueGames,
            Filters = new SeriesFilters
            {
                ThemedOnly = _custThemedOnly, GameIds = _custGameIds.ToList(), ThemeIds = _custThemeIds.ToList(), HolidayIds = _custHolidayIds.ToList(),
                AvoidRecent = _avoidRecent.Count, PlayersTonight = boxes.PlayersTonight, LongestDuration = boxes.LongestDuration
            }
        };
    }

    private Task SendAsync(Func<Task<HttpResponseMessage>> send) => RunAsync(async () =>
    {
        using var response = await send();
        return await ApiResult.ErrorAsync(response);
    });

    // Runs one save (send answers the API's message, or null once saved): its busy guard, the page's message cleared
    // first, the API's message in this dialog (or the page's error bar once it closed), and the close and list reload on
    // success. _createKey is kept on any failure.
    private async Task RunAsync(Func<Task<string?>> send)
    {
        if (_generating) return;
        _generating = true;
        // The dialog this save belongs to; a dismissed or replaced dialog is left alone.
        var token = _customize.Current;
        try
        {
            await PageError.InvokeAsync(null);
            if (await send() is { } error)
            {
                if (_customize.IsCurrent(token)) _customizeError = $"The event was not saved. {error}";
                else await ActionError.InvokeAsync($"The event was not saved. {error}");
                return;
            }

            if (_customize.IsCurrent(token)) CloseCustomize();
            // Saved: the page reloads its list and reports a failed reload itself.
            await Saved.InvokeAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (_customize.IsCurrent(token))
                _customizeError = "The save could not be confirmed. Retry in this dialog to avoid creating a duplicate. If you changed the details, reload the event list and check for the saved event first.";
            else
                await PageError.InvokeAsync("An event save could not be confirmed. Reload the event list and check for the event before creating it again.");
        }
        finally { _generating = false; }
    }
}
