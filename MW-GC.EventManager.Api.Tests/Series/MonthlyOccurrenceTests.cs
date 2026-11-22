using MW_GC.EventManager.Shared.Series;
using static MW_GC.EventManager.Api.Tests.Series.SeriesTestSupport;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// Monthly Series (#204): on the nth weekday (First to Fourth, or Last) and on the same date, a date missing
/// from a short month falling on that month's last day. The calendar is read at the Series' own offset.
/// </summary>
[TestClass]
public sealed class MonthlyOccurrenceTests
{
    [TestMethod]
    public void Second_Thursday_uses_the_Series_offset_even_when_the_UTC_date_is_the_next_day() => AreEqual(
        // 21:00 at -05:00 is 02:00 UTC on the Friday; the rule still picks Thursdays.
        ["2027-01-14T21:00:00-05:00", "2027-02-11T21:00:00-05:00"],
        Generate(RecurrenceRule.MonthlyNthWeekday(WeekOfMonth.Second, DayOfWeek.Thursday),
            first: "2027-01-14T21:00:00-05:00", now: "2027-01-14T12:00:00-05:00"));

    [TestMethod]
    public void Second_Thursday_continues_month_after_month() => AreEqual(
        // The window ends 6 May, so 13 May is not made yet.
        ["2027-03-11T21:00:00-05:00", "2027-04-08T21:00:00-05:00"],
        Generate(RecurrenceRule.MonthlyNthWeekday(WeekOfMonth.Second, DayOfWeek.Thursday),
            first: "2027-01-14T21:00:00-05:00", now: "2027-03-11T12:00:00-05:00"));

    [TestMethod]
    public void First_Monday_and_Third_Saturday_fall_on_their_weekday()
    {
        AreEqual(
            ["2026-12-07T19:00:00-05:00", "2027-01-04T19:00:00-05:00"],
            Generate(RecurrenceRule.MonthlyNthWeekday(WeekOfMonth.First, DayOfWeek.Monday),
                first: "2026-12-07T19:00:00-05:00", now: "2026-12-01T12:00:00-05:00"));
        AreEqual(
            ["2027-05-15T18:00:00+00:00", "2027-06-19T18:00:00+00:00"],
            Generate(RecurrenceRule.MonthlyNthWeekday(WeekOfMonth.Third, DayOfWeek.Saturday),
                first: "2027-05-15T18:00:00+00:00", now: "2027-05-10T12:00:00+00:00"));
    }

    [TestMethod]
    public void Last_Sunday_is_the_fifth_one_in_a_month_that_has_five() => AreEqual(
        // January 2027 has five Sundays (3, 10, 17, 24, 31); Last is the 31st, not the 24th.
        ["2027-01-31T20:00:00-05:00", "2027-02-28T20:00:00-05:00"],
        Generate(RecurrenceRule.MonthlyNthWeekday(WeekOfMonth.Last, DayOfWeek.Sunday),
            first: "2027-01-31T20:00:00-05:00", now: "2027-01-31T10:00:00-05:00"));

    [TestMethod]
    public void Fourth_Sunday_is_not_the_last_one_in_a_month_that_has_five() => AreEqual(
        ["2027-01-24T20:00:00-05:00", "2027-02-28T20:00:00-05:00"],
        Generate(RecurrenceRule.MonthlyNthWeekday(WeekOfMonth.Fourth, DayOfWeek.Sunday),
            first: "2027-01-24T20:00:00-05:00", now: "2027-01-24T10:00:00-05:00"));

    [TestMethod]
    public void A_first_instant_off_the_rule_starts_at_the_next_rule_date_on_or_after_it() => AreEqual(
        // 5 January 2027 is a Tuesday; the second Thursday of January is the 14th.
        ["2027-01-14T21:00:00-05:00", "2027-02-11T21:00:00-05:00"],
        Generate(RecurrenceRule.MonthlyNthWeekday(WeekOfMonth.Second, DayOfWeek.Thursday),
            first: "2027-01-05T21:00:00-05:00", now: "2027-01-01T12:00:00-05:00"));

    [TestMethod]
    public void The_31st_falls_on_the_last_day_of_February_and_returns_to_the_31st() => AreEqual(
        ["2027-01-31T19:00:00-05:00", "2027-02-28T19:00:00-05:00"],
        Generate(RecurrenceRule.MonthlySameDate(), first: "2027-01-31T19:00:00-05:00", now: "2027-01-31T12:00:00-05:00"));

    [TestMethod]
    public void The_31st_falls_on_the_30th_in_April_and_comes_back_in_May()
    {
        AreEqual(
            ["2027-03-31T19:00:00-05:00"],
            Generate(RecurrenceRule.MonthlySameDate(), first: "2027-01-31T19:00:00-05:00", now: "2027-02-28T20:00:00-05:00"));
        AreEqual(
            ["2027-04-30T19:00:00-05:00"],
            Generate(RecurrenceRule.MonthlySameDate(), first: "2027-01-31T19:00:00-05:00", now: "2027-03-31T20:00:00-05:00"));
        AreEqual(
            ["2027-05-31T19:00:00-05:00"],
            Generate(RecurrenceRule.MonthlySameDate(), first: "2027-01-31T19:00:00-05:00", now: "2027-04-30T20:00:00-05:00"));
    }

    [TestMethod]
    public void The_30th_falls_on_February_29_in_a_leap_year() => AreEqual(
        ["2028-01-30T19:00:00-05:00", "2028-02-29T19:00:00-05:00"],
        Generate(RecurrenceRule.MonthlySameDate(), first: "2028-01-30T19:00:00-05:00", now: "2028-01-30T12:00:00-05:00"));

    [TestMethod]
    public void The_15th_stays_on_the_15th() => AreEqual(
        ["2027-06-15T18:00:00+00:00", "2027-07-15T18:00:00+00:00"],
        Generate(RecurrenceRule.MonthlySameDate(), first: "2027-06-15T18:00:00+00:00", now: "2027-06-01T12:00:00+00:00"));
}
