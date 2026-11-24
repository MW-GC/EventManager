using MW_GC.EventManager.Shared.Series;
using static MW_GC.EventManager.Api.Tests.Series.SeriesTestSupport;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// The optional end of a Series (#204 user story 7): the generator never makes an Occurrence after the end,
/// not even the next one beyond the horizon, and makes one exactly at the end.
/// </summary>
[TestClass]
public sealed class SeriesEndDateTests
{
    private static string[] Generate(RecurrenceRule rule, string first, string now, string end, string[]? existing = null) =>
        Render(OccurrenceGenerator.Generate(
            new RecurrenceSeries(At(first), rule, At(end)), At(now), (existing ?? []).Select(At).ToArray(), []));

    [TestMethod]
    public void An_Occurrence_exactly_at_the_end_is_made_and_none_after_it() => AreEqual(
        ["2027-03-04T19:00:00-05:00", "2027-03-11T19:00:00-05:00", "2027-03-18T19:00:00-05:00"],
        Generate(RecurrenceRule.Weekly(), first: "2027-03-04T19:00:00-05:00", now: "2027-03-04T12:00:00-05:00",
            end: "2027-03-18T19:00:00-05:00"));

    [TestMethod]
    public void An_end_one_second_before_a_rule_date_stops_before_it() => AreEqual(
        ["2027-03-04T19:00:00-05:00", "2027-03-11T19:00:00-05:00"],
        Generate(RecurrenceRule.Weekly(), first: "2027-03-04T19:00:00-05:00", now: "2027-03-04T12:00:00-05:00",
            end: "2027-03-18T18:59:59-05:00"));

    [TestMethod]
    public void The_end_compares_as_an_instant_whatever_its_offset() => AreEqual(
        // 18 March 19:00 at -05:00 is midnight UTC on the 19th.
        ["2027-03-04T19:00:00-05:00", "2027-03-11T19:00:00-05:00", "2027-03-18T19:00:00-05:00"],
        Generate(RecurrenceRule.Weekly(), first: "2027-03-04T19:00:00-05:00", now: "2027-03-04T12:00:00-05:00",
            end: "2027-03-19T00:00:00+00:00"));

    [TestMethod]
    public void An_ended_Series_has_no_next_Occurrence_beyond_the_horizon() => AreEqual(
        // Without the end, Halloween 2027 would be made as the next one, ten weeks out.
        [],
        Generate(RecurrenceRule.AnnualHoliday(HolidayPreset.Halloween), first: "2026-10-31T19:00:00-05:00",
            now: "2027-08-22T19:00:00-05:00", end: "2027-06-30T00:00:00-05:00"));

    [TestMethod]
    public void A_Series_that_ended_before_now_makes_nothing() => AreEqual(
        [],
        Generate(RecurrenceRule.Weekly(), first: "2027-03-04T19:00:00-05:00", now: "2027-04-01T12:00:00-05:00",
            end: "2027-03-25T19:00:00-05:00"));

    [TestMethod]
    public void An_end_beyond_the_horizon_changes_nothing() => AreEqual(
        ["2027-03-04T19:00:00-05:00", "2027-03-11T19:00:00-05:00", "2027-03-18T19:00:00-05:00", "2027-03-25T19:00:00-05:00",
            "2027-04-01T19:00:00-05:00", "2027-04-08T19:00:00-05:00", "2027-04-15T19:00:00-05:00", "2027-04-22T19:00:00-05:00"],
        Generate(RecurrenceRule.Weekly(), first: "2027-03-04T19:00:00-05:00", now: "2027-03-04T12:00:00-05:00",
            end: "2027-12-31T00:00:00-05:00"));

    [TestMethod]
    public void A_monthly_Series_stops_at_its_end_too() => AreEqual(
        ["2027-03-11T19:00:00-05:00"],
        Generate(RecurrenceRule.MonthlyNthWeekday(WeekOfMonth.Second, DayOfWeek.Thursday),
            first: "2027-03-11T19:00:00-05:00", now: "2027-03-01T12:00:00-05:00", end: "2027-04-08T18:00:00-05:00"));

    [TestMethod]
    public void No_end_is_the_default_and_keeps_the_old_answer()
    {
        var series = new RecurrenceSeries(At("2027-03-04T19:00:00-05:00"), RecurrenceRule.Weekly());
        Assert.IsNull(series.End);
        Assert.HasCount(8, OccurrenceGenerator.Generate(series, At("2027-03-04T12:00:00-05:00"), [], []));
    }
}
