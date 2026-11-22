namespace MW_GC.EventManager.Shared.Series;

/// <summary>Plain calendar arithmetic on dates, with no clock and no time zone.</summary>
internal static class CalendarDates
{
    /// <summary>The first to fourth, or the last, <paramref name="weekday"/> of a month.</summary>
    public static DateOnly NthWeekday(int year, int month, WeekOfMonth week, DayOfWeek weekday)
    {
        if (week == WeekOfMonth.Last)
        {
            var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
            return last.AddDays(-(((int)last.DayOfWeek - (int)weekday + 7) % 7));
        }

        var first = new DateOnly(year, month, 1);
        var firstMatch = first.AddDays(((int)weekday - (int)first.DayOfWeek + 7) % 7);
        return firstMatch.AddDays(7 * ((int)week - 1));
    }

    /// <summary><paramref name="day"/> of a month, or the month's last day when it is shorter.</summary>
    public static DateOnly ClampedDay(int year, int month, int day) =>
        new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));

    /// <summary>Easter Sunday in the Gregorian calendar (the anonymous Gregorian algorithm, Meeus/Jones/Butcher).</summary>
    public static DateOnly EasterSunday(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateOnly(year, month, day);
    }
}
