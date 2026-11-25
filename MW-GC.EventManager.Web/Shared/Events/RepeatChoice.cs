using MW_GC.EventManager.Shared.Series;

namespace MW_GC.EventManager.Web.Shared.Events;

/// <summary>One choice in a Repeat select: the value it stands for and the words the admin reads.</summary>
public sealed record RepeatOption<T>(T Value, string Name);

/// <summary>
/// The Repeat section of the create Event dialog (#100, #204): whether the new Event repeats, on which rule, and
/// the optional end, turned into the Series' schedule. Pure: no clock, no JS, no services. The dialog keeps one for
/// each opening, so Repeat is off with every opening. Monthly on the nth weekday and Yearly on a holiday take their
/// defaults from the picked date (its weekday, which week of the month it is in, the holiday that falls on it) until
/// the admin picks a value, so moving the date moves an untouched default with it.
/// </summary>
public sealed class RepeatChoice
{
    /// <summary>The API's own wording for an end that is not after the start (SeriesValidator.EndMessage).</summary>
    public const string EndMessage = "End must be after the start.";

    /// <summary>The rules offered, Weekly first, in the words the admin reads. Not daily, not custom.</summary>
    public static IReadOnlyList<RepeatOption<RecurrenceKind>> Rules { get; } =
    [
        new(RecurrenceKind.Weekly, "Weekly"),
        new(RecurrenceKind.BiWeekly, "Every other week"),
        new(RecurrenceKind.MonthlyNthWeekday, "Monthly on the nth weekday"),
        new(RecurrenceKind.MonthlySameDate, "Monthly on the same date"),
        new(RecurrenceKind.AnnualSameDate, "Yearly on the same date"),
        new(RecurrenceKind.AnnualHoliday, "Yearly on a holiday"),
    ];

    /// <summary>The weeks of a month a monthly nth-weekday rule can pick; there is no fifth.</summary>
    public static IReadOnlyList<RepeatOption<WeekOfMonth>> Weeks { get; } =
    [
        new(WeekOfMonth.First, "First"),
        new(WeekOfMonth.Second, "Second"),
        new(WeekOfMonth.Third, "Third"),
        new(WeekOfMonth.Fourth, "Fourth"),
        new(WeekOfMonth.Last, "Last"),
    ];

    /// <summary>The weekdays, Sunday first.</summary>
    public static IReadOnlyList<RepeatOption<DayOfWeek>> Weekdays { get; } =
        Enum.GetValues<DayOfWeek>().Select(d => new RepeatOption<DayOfWeek>(d, d.ToString())).ToList();

    /// <summary>
    /// The one table of readable names for the holiday presets (date rules, not the Holiday tag on Activities), in
    /// calendar order. Not named HolidayPresets: that is the Shared rules table the dates come from.
    /// </summary>
    public static IReadOnlyList<RepeatOption<HolidayPreset>> HolidayPresetOptions { get; } =
    [
        new(HolidayPreset.NewYearsDay, "New Year's Day"),
        new(HolidayPreset.MartinLutherKingJrDay, "Martin Luther King Jr. Day"),
        new(HolidayPreset.ValentinesDay, "Valentine's Day"),
        new(HolidayPreset.PresidentsDay, "Presidents' Day"),
        new(HolidayPreset.StPatricksDay, "St. Patrick's Day"),
        new(HolidayPreset.EarthDay, "Earth Day"),
        new(HolidayPreset.EasterSunday, "Easter Sunday"),
        new(HolidayPreset.MothersDay, "Mother's Day"),
        new(HolidayPreset.MemorialDay, "Memorial Day"),
        new(HolidayPreset.FathersDay, "Father's Day"),
        new(HolidayPreset.IndependenceDay, "Independence Day"),
        new(HolidayPreset.LaborDay, "Labor Day"),
        new(HolidayPreset.ColumbusDay, "Columbus Day / Indigenous Peoples' Day"),
        new(HolidayPreset.Halloween, "Halloween"),
        new(HolidayPreset.Thanksgiving, "Thanksgiving"),
        new(HolidayPreset.ChristmasEve, "Christmas Eve"),
        new(HolidayPreset.Christmas, "Christmas Day"),
        new(HolidayPreset.NewYearsEve, "New Year's Eve"),
    ];

    /// <summary>Whether the new Event repeats. Off until the admin turns it on.</summary>
    public bool IsOn { get; set; }

    public RecurrenceKind Kind { get; set; } = RecurrenceKind.Weekly;

    /// <summary>The week and weekday the admin picked for a monthly nth-weekday rule; null follows the date.</summary>
    public WeekOfMonth? ChosenWeek { get; set; }
    public DayOfWeek? ChosenWeekday { get; set; }

    /// <summary>The holiday preset the admin picked; null follows the date.</summary>
    public HolidayPreset? ChosenHolidayPreset { get; set; }

    /// <summary>The last day an Occurrence may fall on, or null for no end.</summary>
    public DateOnly? EndDate { get; set; }

    public WeekOfMonth WeekFor(DateOnly start) => ChosenWeek ?? DefaultWeek(start);
    public DayOfWeek WeekdayFor(DateOnly start) => ChosenWeekday ?? start.DayOfWeek;
    public HolidayPreset HolidayPresetFor(DateOnly start) => ChosenHolidayPreset ?? DefaultHolidayPreset(start);

    /// <summary>
    /// Which week of its month <paramref name="date"/> is in: First to Fourth by its day (the 1st to 7th is First, and
    /// so on), and Last for a fifth one (the 29th to the 31st), since there is no fifth. A date that is both the fourth
    /// and the last of its weekday (the 22nd to the 28th in the month's last seven days) stays Fourth; Last is always
    /// offered in <see cref="Weeks"/>.
    /// </summary>
    public static WeekOfMonth DefaultWeek(DateOnly date) => ((date.Day - 1) / 7) switch
    {
        0 => WeekOfMonth.First,
        1 => WeekOfMonth.Second,
        2 => WeekOfMonth.Third,
        3 => WeekOfMonth.Fourth,
        _ => WeekOfMonth.Last,
    };

    /// <summary>The holiday preset that falls on <paramref name="date"/>, or the first in the table when none does.</summary>
    public static HolidayPreset DefaultHolidayPreset(DateOnly date) =>
        HolidayPresetOptions.Select(h => h.Value).FirstOrDefault(h => HolidayPresets.DateIn(h, date.Year) == date, HolidayPresetOptions[0].Value);

    /// <summary>The readable name of <paramref name="preset"/>.</summary>
    public static string HolidayPresetName(HolidayPreset preset) => HolidayPresetOptions.Single(h => h.Value == preset).Name;

    /// <summary>The rule the choices make for a Series that starts on <paramref name="start"/>.</summary>
    public RecurrenceRule RuleFor(DateOnly start) => Kind switch
    {
        RecurrenceKind.Weekly => RecurrenceRule.Weekly(),
        RecurrenceKind.BiWeekly => RecurrenceRule.BiWeekly(),
        RecurrenceKind.MonthlyNthWeekday => RecurrenceRule.MonthlyNthWeekday(WeekFor(start), WeekdayFor(start)),
        RecurrenceKind.MonthlySameDate => RecurrenceRule.MonthlySameDate(),
        RecurrenceKind.AnnualSameDate => RecurrenceRule.AnnualSameDate(),
        RecurrenceKind.AnnualHoliday => RecurrenceRule.AnnualHoliday(HolidayPresetFor(start)),
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Repeat must be a known rule."),
    };

    /// <summary>
    /// The end instant: <see cref="EndDate"/> at <paramref name="first"/>'s time of day and UTC offset, the offset the
    /// Series' calendar arithmetic runs at, so an Occurrence on the end date is made and none after it. Null for none.
    /// </summary>
    public DateTimeOffset? EndFor(DateTimeOffset first) => EndDate is { } end
        ? new DateTimeOffset(end.ToDateTime(TimeOnly.FromTimeSpan(first.TimeOfDay)), first.Offset)
        : null;

    /// <summary>The API's message when the end is not after <paramref name="first"/>; null when it is, or there is none.</summary>
    public string? EndProblem(DateTimeOffset first) => EndFor(first) is { } end && end <= first ? EndMessage : null;

    /// <summary>
    /// The Series' schedule: <paramref name="first"/> exactly as given (its offset kept), the rule and the end. The
    /// weekday, week and date the rule reads are <paramref name="first"/>'s own, at its offset.
    /// </summary>
    public RecurrenceSeries ScheduleFor(DateTimeOffset first) =>
        new(first, RuleFor(DateOnly.FromDateTime(first.DateTime)), EndFor(first));

    /// <summary>What the admin chose, for the dialog's unsaved-changes check; defaults that follow the date are not choices.</summary>
    public object Snapshot() => new { IsOn, Kind, ChosenWeek, ChosenWeekday, ChosenHolidayPreset, EndDate };
}
