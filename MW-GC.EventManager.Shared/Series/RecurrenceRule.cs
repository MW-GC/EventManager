namespace MW_GC.EventManager.Shared.Series;

/// <summary>Which calendar rule a Series repeats on.</summary>
public enum RecurrenceKind
{
    Weekly,
    BiWeekly,
    MonthlyNthWeekday,
    MonthlySameDate,
    AnnualSameDate,
    AnnualHoliday,
}

/// <summary>
/// Which of a month's weekdays a monthly nth-weekday rule picks: the first to the fourth, or the last.
/// There is no fifth, because not every month has one.
/// </summary>
public enum WeekOfMonth
{
    Last = -1,
    First = 1,
    Second = 2,
    Third = 3,
    Fourth = 4,
}

/// <summary>
/// The rule a Series repeats on (#204). <see cref="Week"/> and <see cref="Weekday"/> belong to
/// <see cref="RecurrenceKind.MonthlyNthWeekday"/> only and <see cref="Preset"/> to
/// <see cref="RecurrenceKind.AnnualHoliday"/> only, and the other kinds ignore them; those kinds take the
/// weekday, day or date of the Series' first Occurrence. Build one with the factory methods and check it
/// with <see cref="Validate"/>.
/// </summary>
public sealed record RecurrenceRule(
    RecurrenceKind Kind, WeekOfMonth? Week = null, DayOfWeek? Weekday = null, HolidayPreset? Preset = null)
{
    public static RecurrenceRule Weekly() => new(RecurrenceKind.Weekly);

    public static RecurrenceRule BiWeekly() => new(RecurrenceKind.BiWeekly);

    public static RecurrenceRule MonthlyNthWeekday(WeekOfMonth week, DayOfWeek weekday) =>
        new(RecurrenceKind.MonthlyNthWeekday, week, weekday);

    public static RecurrenceRule MonthlySameDate() => new(RecurrenceKind.MonthlySameDate);

    public static RecurrenceRule AnnualSameDate() => new(RecurrenceKind.AnnualSameDate);

    public static RecurrenceRule AnnualHoliday(HolidayPreset preset) =>
        new(RecurrenceKind.AnnualHoliday, Preset: preset);

    /// <summary>The first problem with this rule as a message, or null when the rule is valid.</summary>
    public string? Validate()
    {
        if (!Enum.IsDefined(Kind)) return "Repeat must be a known rule.";

        if (Kind == RecurrenceKind.MonthlyNthWeekday)
        {
            if (Week is not { } week || !Enum.IsDefined(week))
                return "Week of month must be First, Second, Third, Fourth or Last.";
            if (Weekday is not { } weekday || !Enum.IsDefined(weekday))
                return "Weekday must be a day of the week.";
        }

        if (Kind == RecurrenceKind.AnnualHoliday && (Preset is not { } preset || !Enum.IsDefined(preset)))
            return "Holiday preset must be a known preset.";

        return null;
    }
}
