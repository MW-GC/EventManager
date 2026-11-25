using System.Globalization;
using MW_GC.EventManager.Web.Services;
using MW_GC.EventManager.Web.Shared.Activities;

namespace MW_GC.EventManager.Web.Shared.Events;

/// <summary>
/// The players tonight and longest duration Filters (#98) as the Filters panel shows them: the text typed into each
/// box and the number it turns on. Empty is off. Text that is not a whole number from 1 to the limit of the matching
/// Activity field shows the box's message and leaves that Filter off, so a bad value never filters.
/// </summary>
public sealed class PlayersDurationFilters
{
    /// <summary>One box: its label, which is also its accessible name, the name its message uses, and its limit.</summary>
    public sealed record Field(string Id, string Label, string Name, string Placeholder, int Max)
    {
        /// <summary>The message for text that is not a whole number from 1 to <see cref="Max"/>.</summary>
        public string Message => $"{Name} must be a whole number from 1 to {Max}.";

        /// <summary>The id of the element that holds the message, which the box names in aria-describedby.</summary>
        public string MessageId => $"{Id}-message";
    }

    public static readonly Field PlayersTonight = new("filter-players-tonight", "Players tonight", "Players tonight", "e.g. 4",
        PlayersDurationFields.MaxPlayerCount);

    public static readonly Field LongestDuration = new("filter-longest-duration", "Longest duration (minutes)", "Longest duration", "e.g. 60",
        PlayersDurationFields.MaxDurationMinutes);

    /// <summary>The two boxes, in the order the panel shows them.</summary>
    public static IReadOnlyList<Field> All { get; } = [PlayersTonight, LongestDuration];

    private readonly Dictionary<Field, string> _texts = All.ToDictionary(f => f, _ => string.Empty);

    public string Text(Field field) => _texts[field];

    public void Type(Field field, string? text) => _texts[field] = text ?? string.Empty;

    /// <summary>The number the box turns its Filter on with; null when it is empty or its text is not valid.</summary>
    private int? Value(Field field) => Read(field, out var value) ? value : null;

    /// <summary>The box's message while its text is not valid; null when it is empty or valid.</summary>
    public string? Message(Field field) => Read(field, out _) ? null : field.Message;

    /// <summary><paramref name="filter"/> with both Filters as the boxes turn them on.</summary>
    public SlotFilter ApplyTo(SlotFilter filter) =>
        filter with { PlayersTonight = Value(PlayersTonight), LongestDuration = Value(LongestDuration) };

    /// <summary>The typed text of both boxes, compared to tell unsaved changes: text that is not valid counts too.</summary>
    public IReadOnlyList<string> Texts => All.Select(f => _texts[f]).ToList();

    // True when the text is empty (value null) or a whole number from 1 to the box's limit, read as the Activity
    // fields read it (PlayersDurationFields): trimmed, an optional sign, invariant digits.
    private bool Read(Field field, out int? value)
    {
        value = null;
        var trimmed = _texts[field].Trim();
        if (trimmed.Length == 0) return true;
        if (!int.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)) return false;
        if (number < 1 || number > field.Max) return false;
        value = number;
        return true;
    }
}
