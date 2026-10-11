using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;

namespace MW_GC.EventManager.Web.Services;

/// <summary>One slot of the Event being planned: a Game and one of its Activities.</summary>
public sealed record SlotSelection
{
    public Guid GameId { get; set; }
    public Guid ActivityId { get; set; }
    // The Selection as saved in the Event being edited; null for slots made in this dialog.
    public Selection? Stored { get; init; }
}

/// <summary>
/// The filters that limit which Activities a roll may pick. An empty id list means no limit.
/// ThemedOnly keeps Activities with at least one Theme or Holiday. UniqueGames keeps every slot on
/// a different Game.
/// </summary>
public sealed record SlotFilter(bool ThemedOnly, IReadOnlyList<Guid> GameIds, IReadOnlyList<Guid> ThemeIds, IReadOnlyList<Guid> HolidayIds, bool UniqueGames)
{
    /// <summary>What a new customize dialog starts with: no filters, Unique games only on.</summary>
    public static SlotFilter Default { get; } = new(false, [], [], [], true);
}

/// <summary>
/// The slot-planning rules of the customize dialog (issue #58): randomize every slot, re-roll one,
/// add one, change one slot's Game or Activity, remove one, and the candidate lists the pickers
/// offer. Every roll uses the injected <see cref="Random"/>. The library lists are read, never
/// changed; until both are set, every candidate list is empty.
/// </summary>
public sealed class SlotPlanner(Random random)
{
    public const int DefaultSlotCount = 3;
    public const string SelectionsKeptError = "Not enough distinct activities or games match the current filters. Existing selections were kept.";
    public const string NoMatchError = "No activities match the current filters. Adjust the filters or add activities to the library.";
    public const string AddSlotFullReason = "An Event holds at most 5 activities.";
    public const string AddSlotUniqueGamesReason = "Every Game with an unused activity is already in a slot. Turn off Unique games only to repeat a Game.";
    public const string AddSlotNoMatchReason = "No unused activity matches the current filters. Adjust the filters or add activities to the library.";

    private IReadOnlyList<GameEntity>? _games;
    private IReadOnlyList<ActivityEntity>? _activities;
    private List<SlotSelection> _slots = [];

    /// <summary>The filters every roll and candidate list applies.</summary>
    public SlotFilter Filter { get; set; } = SlotFilter.Default;

    /// <summary>The slots in order. A failed roll keeps this very list.</summary>
    public IReadOnlyList<SlotSelection> Slots => _slots;

    /// <summary>Why the last Randomize All found nothing; cleared by the next change to the slots.</summary>
    public string? GenerationError { get; set; }

    /// <summary>Set when Add Activity Slot is invoked with nothing to add.</summary>
    public string? AddSlotError { get; set; }

    /// <summary>The library to roll from. Null means not loaded yet.</summary>
    public void SetLibrary(IReadOnlyList<GameEntity>? games, IReadOnlyList<ActivityEntity>? activities)
    {
        _games = games;
        _activities = activities;
    }

    /// <summary>Replaces the slots, for a new dialog or an Event being edited.</summary>
    public void SetSlots(IEnumerable<SlotSelection> slots) => _slots = slots.ToList();

    // A failed generation leaves the current selections untouched, so only claim they
    // were kept when there are some to keep.
    private void SetGenerationFailure() =>
        GenerationError = _slots.Count > 0 ? SelectionsKeptError : NoMatchError;

    public void RandomizeAll()
    {
        var rng = random;
        var available = GetFilteredActivities();
        var grouped = available.GroupBy(a => a.GameId).ToDictionary(g => g.Key, g => g.ToList());
        var uniqueGames = Filter.UniqueGames;
        int count;
        if (_slots.Count > 0)
        {
            count = _slots.Count;
        }
        else
        {
            // Fresh dialog: aim for the default slot count but settle for what the library can fill.
            var capacity = uniqueGames ? grouped.Count : available.Count;
            if (capacity == 0)
            {
                SetGenerationFailure();
                return;
            }
            count = Math.Min(DefaultSlotCount, capacity);
        }

        if (uniqueGames)
        {
            var matched = MatchUniqueGames(grouped, count, rng);
            if (matched is null)
            {
                SetGenerationFailure();
                return;
            }
            _slots = matched;
            GenerationError = null;
            return;
        }

        var gameIds = grouped.Keys.ToList();
        var newSelections = new List<SlotSelection>();
        var usedGames = new HashSet<Guid>();
        var usedActivities = new HashSet<Guid>();

        for (var i = 0; i < count; i++)
        {
            var pool = gameIds.Where(g =>
                (!uniqueGames || !usedGames.Contains(g)) &&
                grouped[g].Any(a => !usedActivities.Contains(a.Id))).ToList();
            if (pool.Count == 0)
            {
                SetGenerationFailure();
                return;
            }
            var gameId = pool[rng.Next(pool.Count)];
            var acts = grouped[gameId].Where(a => !usedActivities.Contains(a.Id)).ToList();
            var act = acts[rng.Next(acts.Count)];
            newSelections.Add(new SlotSelection { GameId = gameId, ActivityId = act.Id });
            usedGames.Add(gameId);
            usedActivities.Add(act.Id);
        }

        _slots = newSelections;
        GenerationError = null;
    }

    /// <summary>
    /// Fills <paramref name="count"/> slots, each with a different Game and a different Activity id,
    /// or returns null when no such assignment exists.
    /// </summary>
    public static List<SlotSelection>? MatchUniqueGames(Dictionary<Guid, List<ActivityEntity>> grouped, int count, Random rng)
    {
        if (grouped.Count < count) return null;

        // Randomize preferences, not feasibility. Repair earlier assignments via
        // augmenting paths and consider every game before declaring exhaustion.
        var games = grouped.Keys.OrderBy(_ => rng.Next()).ToArray();
        var choices = games.ToDictionary(g => g,
            g => grouped[g].Select(a => a.Id).Distinct().OrderBy(_ => rng.Next()).ToArray());
        var byActivity = new Dictionary<Guid, Guid>();
        var byGame = new Dictionary<Guid, Guid>();
        foreach (var game in games)
        {
            TryAugment(game);
            if (byGame.Count == count)
                return byGame.Select(pair => new SlotSelection { GameId = pair.Key, ActivityId = pair.Value }).ToList();
        }
        return null;

        void TryAugment(Guid start)
        {
            // Iterative traversal avoids call-stack growth on long repair chains.
            var paths = new Dictionary<Guid, Guid>();
            var pending = new Queue<Guid>();
            pending.Enqueue(start);
            while (pending.TryDequeue(out var game))
            {
                foreach (var activity in choices[game])
                {
                    if (!paths.TryAdd(activity, game)) continue;
                    if (byActivity.TryGetValue(activity, out var owner))
                    {
                        pending.Enqueue(owner);
                        continue;
                    }

                    var next = activity;
                    while (true)
                    {
                        var reachedGame = paths[next];
                        var hadPrevious = byGame.TryGetValue(reachedGame, out var previous);
                        byGame[reachedGame] = next;
                        byActivity[next] = reachedGame;
                        if (!hadPrevious) return;
                        next = previous;
                    }
                }
            }
        }
    }

    // The Activity and Game ids held by every slot except freeSlot (pass -1 to count every slot).
    // Each operation computes these once.
    private (HashSet<Guid> Activities, HashSet<Guid> Games) UsedBy(int freeSlot)
    {
        var others = _slots.Where((_, i) => i != freeSlot).ToList();
        return (others.Select(s => s.ActivityId).ToHashSet(), others.Select(s => s.GameId).ToHashSet());
    }

    // The one candidate pool for changing a single slot: Activities that pass the filters,
    // are not in any other slot and, with Unique games only on, belong to a Game no other
    // slot uses.
    private static List<ActivityEntity> SlotCandidates(List<ActivityEntity> filtered,
        (HashSet<Guid> Activities, HashSet<Guid> Games) used, bool uniqueGames) =>
        filtered.Where(a =>
            !used.Activities.Contains(a.Id) &&
            (!uniqueGames || !used.Games.Contains(a.GameId))).ToList();

    /// <summary>What a re-roll of slot <paramref name="index"/> may pick: never the slot's own Activity.</summary>
    public List<ActivityEntity> GetRerollCandidates(int index)
    {
        if (index < 0 || index >= _slots.Count) return [];
        var current = _slots[index].ActivityId;
        return SlotCandidates(GetFilteredActivities(), UsedBy(index), Filter.UniqueGames).Where(a => a.Id != current).ToList();
    }

    /// <summary>What Add Activity Slot may pick: the re-roll pool with every slot kept.</summary>
    public List<ActivityEntity> GetAddSlotCandidates() =>
        SlotCandidates(GetFilteredActivities(), UsedBy(-1), Filter.UniqueGames);

    /// <summary>Null when Add Activity Slot can add a slot; otherwise the reason it cannot.</summary>
    public string? GetAddSlotBlockedReason() => AddSlotBlockedReason(out _);

    private string? AddSlotBlockedReason(out List<ActivityEntity> candidates)
    {
        candidates = [];
        if (_slots.Count >= EventEntity.MaximumSelections) return AddSlotFullReason;
        var filtered = GetFilteredActivities();
        var used = UsedBy(-1);
        candidates = SlotCandidates(filtered, used, Filter.UniqueGames);
        if (candidates.Count > 0) return null;
        return Filter.UniqueGames && SlotCandidates(filtered, used, uniqueGames: false).Count > 0
            ? AddSlotUniqueGamesReason
            : AddSlotNoMatchReason;
    }

    public void RerollSlot(int index)
    {
        // Pick a game first, as Randomize All does, so games with larger
        // activity catalogs do not dominate the roll. Never touch other slots.
        var groups = GetRerollCandidates(index).GroupBy(a => a.GameId).ToList();
        if (groups.Count == 0) return;
        var activities = groups[random.Next(groups.Count)].ToList();
        var activity = activities[random.Next(activities.Count)];
        _slots[index] = _slots[index] with { GameId = activity.GameId, ActivityId = activity.Id };
        GenerationError = null;
    }

    // The one activity filter: Activities of a known Game that pass every selected filter.
    private List<ActivityEntity> GetFilteredActivities()
    {
        if (_activities is null || _games is null) return [];
        var filter = Filter;
        var knownGames = _games.Select(g => g.Id).ToHashSet();
        IEnumerable<ActivityEntity> q = _activities.Where(a => knownGames.Contains(a.GameId));
        if (filter.GameIds.Count > 0)
            q = q.Where(a => filter.GameIds.Contains(a.GameId));
        if (filter.ThemedOnly)
            q = q.Where(a => a.ThemeIds.Count > 0 || a.HolidayIds.Count > 0);
        if (filter.ThemeIds.Count > 0)
            q = q.Where(a => a.ThemeIds.Any(t => filter.ThemeIds.Contains(t)));
        if (filter.HolidayIds.Count > 0)
            q = q.Where(a => a.HolidayIds.Any(h => filter.HolidayIds.Contains(h)));
        return q.ToList();
    }

    /// <summary>The Games slot <paramref name="slotIndex"/> may switch to.</summary>
    public List<GameEntity> GetAvailableGames(int slotIndex)
    {
        if (_games is null || _activities is null) return [];
        var used = UsedBy(slotIndex);
        var filtered = GetFilteredActivities().Where(a => !used.Activities.Contains(a.Id));
        var gameIds = filtered.Select(a => a.GameId).Distinct().ToHashSet();
        var available = _games.Where(g => gameIds.Contains(g.Id));
        if (Filter.UniqueGames)
            available = available.Where(g => !used.Games.Contains(g.Id));
        return available.ToList();
    }

    /// <summary>The Activities of <paramref name="gameId"/> slot <paramref name="slotIndex"/> may switch to.</summary>
    public List<ActivityEntity> GetAvailableActivities(Guid gameId, int slotIndex)
    {
        var filtered = GetFilteredActivities().Where(a => a.GameId == gameId);
        var used = UsedBy(slotIndex);
        return filtered.Where(a => !used.Activities.Contains(a.Id)).ToList();
    }

    public void ChangeSlotGame(int index, GameEntity? game)
    {
        if (game is null) return;
        var acts = GetAvailableActivities(game.Id, index);
        if (acts.Count == 0) return;
        var act = acts[random.Next(acts.Count)];
        _slots[index] = _slots[index] with { GameId = game.Id, ActivityId = act.Id };
        GenerationError = null;
    }

    public void ChangeSlotActivity(int index, ActivityEntity? activity)
    {
        if (activity is null) return;
        _slots[index] = _slots[index] with { ActivityId = activity.Id };
        GenerationError = null;
    }

    public void AddSlot()
    {
        // The button is disabled with this reason; a stale click still says why nothing was added.
        if (AddSlotBlockedReason(out var candidates) is { } reason)
        {
            AddSlotError = reason;
            return;
        }

        // Pick a game first, as re-roll does, so games with larger catalogs do not dominate.
        var groups = candidates.GroupBy(a => a.GameId).ToList();
        var acts = groups[random.Next(groups.Count)].ToList();
        var act = acts[random.Next(acts.Count)];
        _slots.Add(new SlotSelection { GameId = act.GameId, ActivityId = act.Id });
        AddSlotError = null;
        GenerationError = null;
    }

    public void RemoveSlot(int index)
    {
        if (_slots.Count <= 1) return;
        _slots.RemoveAt(index);
        GenerationError = null;
    }
}
