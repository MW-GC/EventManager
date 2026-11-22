namespace MW_GC.EventManager.Shared.Series;

/// <summary>
/// Works out which Occurrences of a Series to create now (#204, ADR 0002). Pure: no clock, no storage, no
/// randomness, and no time zone database. Calendar arithmetic runs at the UTC offset of the Series' first
/// Occurrence, so every Occurrence is an instant with the same UTC time of day.
/// </summary>
public static class OccurrenceGenerator
{
    /// <summary>How far ahead of now Occurrences are made.</summary>
    public static readonly TimeSpan Horizon = TimeSpan.FromDays(7 * 8);

    /// <summary>
    /// The instants of the Occurrences to create, in order: every rule date from the later of the first
    /// instant and <paramref name="now"/> up to and including <paramref name="now"/> plus <see cref="Horizon"/>,
    /// and the first rule date after <paramref name="now"/> even when it is further out. An instant in
    /// <paramref name="existing"/> is not returned again; one in <paramref name="skipped"/> (a cancelled
    /// Occurrence) is never returned and does not count as the next one. Keys compare as instants, whatever
    /// offset they are written in.
    /// </summary>
    /// <exception cref="ArgumentException">The Series' rule is invalid; the message is <see cref="RecurrenceRule.Validate"/>'s.</exception>
    public static IReadOnlyList<DateTimeOffset> Generate(
        RecurrenceSeries series, DateTimeOffset now,
        IReadOnlyCollection<DateTimeOffset> existing, IReadOnlyCollection<DateTimeOffset> skipped)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(series.Rule);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(skipped);
        if (series.Rule.Validate() is { } problem) throw new ArgumentException(problem, nameof(series));

        var existingKeys = existing.ToHashSet();
        var skippedKeys = skipped.ToHashSet();
        var from = series.First > now ? series.First : now;
        var end = now + Horizon;
        var result = new List<DateTimeOffset>();
        var hasNextAfterNow = false;

        foreach (var instant in RuleDates(series, from))
        {
            if (instant > end && hasNextAfterNow) break;
            if (skippedKeys.Contains(instant)) continue;
            if (instant > now) hasNextAfterNow = true;
            if (!existingKeys.Contains(instant)) result.Add(instant);
        }

        return result;
    }

    /// <summary>
    /// Every rule date at or after <paramref name="from"/>, which is never before the first instant, in order
    /// and without end.
    /// </summary>
    private static IEnumerable<DateTimeOffset> RuleDates(RecurrenceSeries series, DateTimeOffset from) =>
        series.Rule.Kind switch
        {
            RecurrenceKind.Weekly => Stepped(series.First, TimeSpan.FromDays(7), from),
            RecurrenceKind.BiWeekly => Stepped(series.First, TimeSpan.FromDays(14), from),
            _ => Monthly(series, from),
        };

    /// <summary>
    /// <paramref name="first"/> plus whole steps of days. Adding to a <see cref="DateTimeOffset"/> keeps its
    /// offset, so the wall clock at that offset, and the UTC time, never move.
    /// </summary>
    private static IEnumerable<DateTimeOffset> Stepped(DateTimeOffset first, TimeSpan step, DateTimeOffset from)
    {
        var steps = from > first ? (from - first).Ticks / step.Ticks : 0;
        for (var instant = first + step * steps; ; instant += step)
            if (instant >= from) yield return instant;
    }

    /// <summary>
    /// The rule's date in each month from <paramref name="from"/>'s month on, read at the first instant's
    /// offset and given the first instant's time of day.
    /// </summary>
    private static IEnumerable<DateTimeOffset> Monthly(RecurrenceSeries series, DateTimeOffset from)
    {
        var first = series.First;
        var timeOfDay = TimeOnly.FromTimeSpan(first.TimeOfDay);
        var start = from.ToOffset(first.Offset);
        for (var month = new DateOnly(start.Year, start.Month, 1); ; month = month.AddMonths(1))
        {
            if (DateIn(series.Rule, first, month) is not { } date) continue;
            var instant = new DateTimeOffset(date.ToDateTime(timeOfDay), first.Offset);
            if (instant >= from) yield return instant;
        }
    }

    /// <summary>The rule's date in the month starting <paramref name="month"/>, or null when it has none there.</summary>
    private static DateOnly? DateIn(RecurrenceRule rule, DateTimeOffset first, DateOnly month) => rule.Kind switch
    {
        RecurrenceKind.MonthlyNthWeekday =>
            CalendarDates.NthWeekday(month.Year, month.Month, rule.Week!.Value, rule.Weekday!.Value),
        RecurrenceKind.MonthlySameDate => CalendarDates.ClampedDay(month.Year, month.Month, first.Day),
        RecurrenceKind.AnnualSameDate => month.Month == first.Month
            ? CalendarDates.ClampedDay(month.Year, month.Month, first.Day)
            : null,
        RecurrenceKind.AnnualHoliday => HolidayPresets.DateIn(rule.Preset!.Value, month.Year) is var holiday
            && holiday.Month == month.Month
                ? holiday
                : null,
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule.Kind, "Repeat must be a known rule."),
    };
}
