using static MW_GC.EventManager.Shared.Series.CalendarDates;

namespace MW_GC.EventManager.Shared.Series;

/// <summary>
/// The in-house table behind <see cref="HolidayPreset"/> (#204, ADR 0002): one rule per preset, no external
/// service and no time zone. Adding a preset is one enum member and one row here.
/// </summary>
public static class HolidayPresets
{
    private static readonly IReadOnlyDictionary<HolidayPreset, Func<int, DateOnly>> Table =
        new Dictionary<HolidayPreset, Func<int, DateOnly>>
        {
            [HolidayPreset.NewYearsDay] = y => new DateOnly(y, 1, 1),
            [HolidayPreset.MartinLutherKingJrDay] = y => NthWeekday(y, 1, WeekOfMonth.Third, DayOfWeek.Monday),
            [HolidayPreset.ValentinesDay] = y => new DateOnly(y, 2, 14),
            [HolidayPreset.PresidentsDay] = y => NthWeekday(y, 2, WeekOfMonth.Third, DayOfWeek.Monday),
            [HolidayPreset.StPatricksDay] = y => new DateOnly(y, 3, 17),
            [HolidayPreset.EarthDay] = y => new DateOnly(y, 4, 22),
            [HolidayPreset.EasterSunday] = EasterSunday,
            [HolidayPreset.MothersDay] = y => NthWeekday(y, 5, WeekOfMonth.Second, DayOfWeek.Sunday),
            [HolidayPreset.MemorialDay] = y => NthWeekday(y, 5, WeekOfMonth.Last, DayOfWeek.Monday),
            [HolidayPreset.FathersDay] = y => NthWeekday(y, 6, WeekOfMonth.Third, DayOfWeek.Sunday),
            [HolidayPreset.IndependenceDay] = y => new DateOnly(y, 7, 4),
            [HolidayPreset.LaborDay] = y => NthWeekday(y, 9, WeekOfMonth.First, DayOfWeek.Monday),
            [HolidayPreset.ColumbusDay] = y => NthWeekday(y, 10, WeekOfMonth.Second, DayOfWeek.Monday),
            [HolidayPreset.Halloween] = y => new DateOnly(y, 10, 31),
            [HolidayPreset.Thanksgiving] = y => NthWeekday(y, 11, WeekOfMonth.Fourth, DayOfWeek.Thursday),
            [HolidayPreset.ChristmasEve] = y => new DateOnly(y, 12, 24),
            [HolidayPreset.Christmas] = y => new DateOnly(y, 12, 25),
            [HolidayPreset.NewYearsEve] = y => new DateOnly(y, 12, 31),
        };

    /// <summary>The date of <paramref name="preset"/> in <paramref name="year"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The preset is not in the table.</exception>
    public static DateOnly DateIn(HolidayPreset preset, int year) =>
        Table.TryGetValue(preset, out var rule)
            ? rule(year)
            : throw new ArgumentOutOfRangeException(nameof(preset), preset, "Holiday preset must be a known preset.");
}
