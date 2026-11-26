using MW_GC.EventManager.Shared.Series;
using static MW_GC.EventManager.Api.Tests.Series.SeriesTestSupport;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// The picked start of a Series is its anchor, not always an Occurrence (#233.1, ADR 0002): the first Occurrence is
/// the first rule date at or after it. Weekly and every other week step from the anchor, so it is always made; a
/// monthly, yearly or holiday rule makes the anchor only when it falls on the rule's date, and otherwise starts later.
/// Every expected instant is written out by hand: 5 January 2027 is a Tuesday, the second Thursday of January 2027 is
/// the 14th and of February the 11th, and 10 December 2026 is a Thursday that is no holiday.
/// </summary>
[TestClass]
public sealed class FirstOccurrenceTests
{
    private const string TuesdayAnchor = "2027-01-05T21:00:00-05:00";

    [TestMethod]
    [DataRow(RecurrenceKind.Weekly, "2027-01-12T21:00:00-05:00")]
    [DataRow(RecurrenceKind.BiWeekly, "2027-01-19T21:00:00-05:00")]
    public void Weekly_and_every_other_week_make_the_picked_instant_itself(RecurrenceKind kind, string second)
    {
        var made = Generate(new RecurrenceRule(kind), first: TuesdayAnchor, now: "2027-01-01T12:00:00-05:00");

        Assert.AreEqual(TuesdayAnchor, made[0]);
        Assert.AreEqual(second, made[1]);
    }

    // The picked date is the rule's own date, so it is made; the yearly one's next date is a year out, past the window.
    [TestMethod]
    [DataRow(RecurrenceKind.MonthlySameDate)]
    [DataRow(RecurrenceKind.AnnualSameDate)]
    public void A_same_date_rule_makes_the_picked_instant_itself(RecurrenceKind kind) => Assert.AreEqual(
        TuesdayAnchor,
        Generate(new RecurrenceRule(kind), first: TuesdayAnchor, now: "2027-01-01T12:00:00-05:00")[0]);

    // 20 January 2027 is a Wednesday after that month's second Thursday (the 14th), so the first is in February.
    [TestMethod]
    public void An_nth_weekday_already_past_in_the_picked_month_starts_the_next_month() => AreEqual(
        ["2027-02-11T21:00:00-05:00"],
        Generate(RecurrenceRule.MonthlyNthWeekday(WeekOfMonth.Second, DayOfWeek.Thursday),
            first: "2027-01-20T21:00:00-05:00", now: "2027-01-01T12:00:00-05:00"));

    // The picked Tuesday is not the rule's date: it is not made, and the second Thursday later that month is.
    [TestMethod]
    public void An_nth_weekday_off_the_picked_date_never_makes_the_picked_instant()
    {
        var made = Generate(RecurrenceRule.MonthlyNthWeekday(WeekOfMonth.Second, DayOfWeek.Thursday),
            first: TuesdayAnchor, now: "2027-01-01T12:00:00-05:00");

        CollectionAssert.DoesNotContain(made, TuesdayAnchor);
        Assert.AreEqual("2027-01-14T21:00:00-05:00", made[0]);
    }

    // A holiday rule on a date that is no holiday (the create dialog's New Year's Day default) starts at the next one.
    [TestMethod]
    public void A_holiday_rule_off_the_picked_date_starts_at_the_next_holiday() => AreEqual(
        ["2027-01-01T19:00:00-06:00"],
        Generate(RecurrenceRule.AnnualHoliday(HolidayPreset.NewYearsDay),
            first: "2026-12-10T19:00:00-06:00", now: "2026-12-01T12:00:00-06:00"));
}
