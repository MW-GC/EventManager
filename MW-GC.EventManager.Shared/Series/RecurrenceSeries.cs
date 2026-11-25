namespace MW_GC.EventManager.Shared.Series;

/// <summary>
/// What the Occurrence generator needs to know about a Series (#204): the instant of its first Occurrence,
/// with the UTC offset it was entered in, its rule, and its optional end. A plain value, not a stored entity
/// by itself (a <c>SeriesEntity</c> stores one as JSON, which keeps <see cref="First"/>'s offset). Calendar
/// arithmetic (weekday, nth weekday, date, holiday date, time of day) happens at <see cref="First"/>'s
/// offset, so every Occurrence keeps the same UTC time even where the viewer's clock changes for daylight
/// saving (ADR 0002). <see cref="End"/> is the last instant an Occurrence may fall on: one exactly at
/// <see cref="End"/> is made, one after it never is; null means the Series has no end.
/// </summary>
public sealed record RecurrenceSeries(DateTimeOffset First, RecurrenceRule Rule, DateTimeOffset? End = null);
