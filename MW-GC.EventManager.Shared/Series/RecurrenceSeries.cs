namespace MW_GC.EventManager.Shared.Series;

/// <summary>
/// What the Occurrence generator needs to know about a Series (#204): the instant of its first Occurrence,
/// with the UTC offset it was entered in, and its rule. A plain value, not a stored entity. Calendar
/// arithmetic (weekday, nth weekday, date, holiday date, time of day) happens at <see cref="First"/>'s
/// offset, so every Occurrence keeps the same UTC time even where the viewer's clock changes for daylight
/// saving (ADR 0002).
/// </summary>
public sealed record RecurrenceSeries(DateTimeOffset First, RecurrenceRule Rule);
