using MW_GC.EventManager.Shared.Series;

namespace MW_GC.EventManager.Shared.Entities;

/// <summary>
/// A Series (#204, ADR 0002): the rule its Occurrences are generated from and the slot template they start
/// with when rolled. Stored as its own row in the <c>Series</c> table with its own eTag, apart from its
/// Occurrences, which are ordinary Event rows (ADR 0001's pattern).
/// </summary>
public class SeriesEntity : EntityBase
{
    /// <summary>The name every Occurrence gets when it is generated.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The instant of the first Occurrence with the UTC offset it was entered in, the rule and the optional
    /// end. Stored as one JSON value: Table Storage keeps a date only as UTC, and the offset is what the
    /// calendar arithmetic runs at, so it must survive the round trip.
    /// </summary>
    public RecurrenceSeries Schedule { get; set; } = new(default, RecurrenceRule.Weekly());

    /// <summary>How many slots an Occurrence starts with when it is rolled: 1 to 5.</summary>
    public int SlotCount { get; set; } = 3;

    /// <summary>The Unique games only setting every Occurrence gets.</summary>
    public bool UniqueGamesOnly { get; set; } = true;

    /// <summary>The Filters an Occurrence starts with when it is rolled.</summary>
    public SeriesFilters Filters { get; set; } = new();

    /// <summary>
    /// The occurrence keys of cancelled Occurrences, which the generator never makes again, even after an
    /// edit whose rule still produces them. Owned by the API: a request body's list is ignored. Stored as
    /// JSON and split into numbered chunks when long, like an Event's Selections.
    /// </summary>
    public List<DateTimeOffset> Skipped { get; set; } = [];
}

/// <summary>
/// The Filters of a Series' slot template, the same choices as the Events page's Filters: specific Games,
/// Themes or Holidays (an empty list means no limit) and Themed activities only.
/// </summary>
public class SeriesFilters
{
    public bool ThemedOnly { get; set; }
    public List<Guid> GameIds { get; set; } = [];
    public List<Guid> ThemeIds { get; set; } = [];
    public List<Guid> HolidayIds { get; set; } = [];
}
