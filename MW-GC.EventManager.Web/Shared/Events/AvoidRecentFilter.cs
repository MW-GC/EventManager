using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Shared.Events;

/// <summary>
/// The avoid-recent Filter (#95) as the Filters panel shows it: a switch and a box for N, both kept as the text of the
/// box. Empty is off. Switching on writes 3, switching off empties the box. A whole number from 1 to 10 turns the
/// Filter on with that N; anything else shows the box's message and leaves the Filter off, so a bad value never
/// filters. The text is read by the same reader as the players tonight and longest duration boxes.
/// </summary>
public sealed class AvoidRecentFilter
{
    /// <summary>N when the Filter is switched on.</summary>
    public const int DefaultCount = 3;

    /// <summary>The switch's label, which is also its accessible name.</summary>
    public const string SwitchLabel = "Avoid recent";

    /// <summary>The box for N: its label (also its accessible name), the name its message uses, and its limit.</summary>
    public static readonly PlayersDurationFilters.Field Box = new("filter-avoid-recent", "Recent Events to avoid", SwitchLabel, "e.g. 3",
        RecentActivities.MaxCount);

    /// <summary>The typed text of the box, compared to tell unsaved changes: text that is not valid counts too.</summary>
    public string Text { get; private set; } = string.Empty;

    public void Type(string? text) => Text = text ?? string.Empty;

    /// <summary>The switch: on writes the default N, off empties the box.</summary>
    public void Switch(bool on) => Text = on ? DefaultCount.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>N while the Filter is on; null when the box is empty or its text is not valid.</summary>
    public int? Count => PlayersDurationFilters.ReadWholeNumber(Text, Box.Max, out var value) ? value : null;

    /// <summary>What the switch shows: on only while a valid N is in the box.</summary>
    public bool IsOn => Count is not null;

    /// <summary>The box's message while its text is not valid; null when it is empty or valid.</summary>
    public string? Message => PlayersDurationFilters.ReadWholeNumber(Text, Box.Max, out _) ? null : Box.Message;

    /// <summary>
    /// <paramref name="filter"/> with the recent Activity ids: the Activities of the last N Events dated before
    /// <paramref name="planned"/>, not counting <paramref name="editing"/>. Null (off) while there is no valid N.
    /// </summary>
    public SlotFilter ApplyTo(SlotFilter filter, IEnumerable<EventEntity> events, DateTimeOffset planned, Guid? editing) =>
        filter with { RecentActivityIds = Count is { } n ? RecentActivities.Ids(events, planned, editing, n) : null };
}
