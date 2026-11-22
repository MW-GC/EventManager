using MW_GC.EventManager.Shared.Series;
using static MW_GC.EventManager.Api.Tests.Series.SeriesTestSupport;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// Which Occurrences the generator makes now (#204): every rule date from the later of the first instant and
/// now up to and including now plus 8 weeks, and always at least the next one, however far out; never one
/// before now, never one that already exists or was cancelled (skipped). A key is the instant itself, so
/// the same instant written at another offset is the same Occurrence.
/// </summary>
[TestClass]
public sealed class OccurrenceHorizonTests
{
    [TestMethod]
    public void A_rule_date_exactly_eight_weeks_out_is_made() => AreEqual(
        // Now plus 8 weeks is 26 April 19:00, the 26th's own instant.
        ["2027-03-26T19:00:00-05:00", "2027-04-26T19:00:00-05:00"],
        Generate(RecurrenceRule.MonthlySameDate(), first: "2027-01-26T19:00:00-05:00", now: "2027-03-01T19:00:00-05:00"));

    [TestMethod]
    public void A_rule_date_one_second_past_eight_weeks_waits() => AreEqual(
        ["2027-03-26T19:00:00-05:00"],
        Generate(RecurrenceRule.MonthlySameDate(), first: "2027-01-26T19:00:00-05:00", now: "2027-03-01T18:59:59-05:00"));

    [TestMethod]
    public void A_rule_date_at_now_is_made_and_one_before_now_is_not()
    {
        AreEqual(
            ["2027-03-04T19:00:00-05:00", "2027-03-18T19:00:00-05:00"],
            Generate(RecurrenceRule.BiWeekly(), first: "2027-02-18T19:00:00-05:00", now: "2027-03-04T19:00:00-05:00")[..2]);
        AreEqual(
            ["2027-03-18T19:00:00-05:00", "2027-04-01T19:00:00-05:00"],
            Generate(RecurrenceRule.BiWeekly(), first: "2027-02-18T19:00:00-05:00", now: "2027-03-04T19:00:01-05:00")[..2]);
    }

    [TestMethod]
    public void A_yearly_date_ten_weeks_out_is_still_made_as_the_next_one() => AreEqual(
        ["2027-10-31T19:00:00-05:00"],
        Generate(RecurrenceRule.AnnualHoliday(HolidayPreset.Halloween),
            first: "2026-10-31T19:00:00-05:00", now: "2027-08-22T19:00:00-05:00"));

    [TestMethod]
    public void A_Series_that_starts_ten_weeks_out_gets_its_first_Occurrence_and_no_more() => AreEqual(
        ["2027-05-13T19:00:00-05:00"],
        Generate(RecurrenceRule.Weekly(), first: "2027-05-13T19:00:00-05:00", now: "2027-03-04T19:00:00-05:00"));

    [TestMethod]
    public void A_Series_that_starts_inside_the_window_starts_at_its_first_instant() => AreEqual(
        ["2027-04-15T19:00:00-05:00", "2027-04-22T19:00:00-05:00"],
        Generate(RecurrenceRule.Weekly(), first: "2027-04-15T19:00:00-05:00", now: "2027-03-01T12:00:00-05:00"));

    [TestMethod]
    public void Existing_Occurrences_are_not_made_again() => AreEqual(
        ["2027-03-11T19:00:00-05:00", "2027-04-01T19:00:00-05:00", "2027-04-22T19:00:00-05:00"],
        Generate(RecurrenceRule.Weekly(), first: "2027-03-04T19:00:00-05:00", now: "2027-03-04T12:00:00-05:00",
            existing:
            [
                "2027-03-04T19:00:00-05:00", "2027-03-18T19:00:00-05:00", "2027-03-25T19:00:00-05:00",
                "2027-04-08T19:00:00-05:00", "2027-04-15T19:00:00-05:00",
            ]));

    [TestMethod]
    public void A_key_at_another_offset_is_the_same_Occurrence() => AreEqual(
        // 4 March 19:00 at -05:00 is midnight UTC on the 5th.
        ["2027-03-11T19:00:00-05:00"],
        Generate(RecurrenceRule.Weekly(), first: "2027-03-04T19:00:00-05:00", now: "2027-03-04T12:00:00-05:00",
            existing: ["2027-03-05T00:00:00+00:00"],
            skipped: ["2027-03-19T00:00:00+00:00", "2027-03-26T00:00:00+00:00", "2027-04-02T00:00:00+00:00",
                "2027-04-09T00:00:00+00:00", "2027-04-16T00:00:00+00:00", "2027-04-23T00:00:00+00:00"]));

    [TestMethod]
    public void Skipped_dates_are_never_made() => AreEqual(
        ["2027-03-04T19:00:00-05:00", "2027-03-18T19:00:00-05:00", "2027-03-25T19:00:00-05:00",
            "2027-04-08T19:00:00-05:00", "2027-04-15T19:00:00-05:00", "2027-04-22T19:00:00-05:00"],
        Generate(RecurrenceRule.Weekly(), first: "2027-03-04T19:00:00-05:00", now: "2027-03-04T12:00:00-05:00",
            skipped: ["2027-03-11T19:00:00-05:00", "2027-04-01T19:00:00-05:00"]));

    [TestMethod]
    public void Running_twice_makes_nothing_new()
    {
        var first = Generate(RecurrenceRule.Weekly(), first: "2027-03-04T19:00:00-05:00", now: "2027-03-04T12:00:00-05:00");
        Assert.HasCount(8, first);
        AreEqual([], Generate(RecurrenceRule.Weekly(), first: "2027-03-04T19:00:00-05:00",
            now: "2027-03-04T12:00:00-05:00", existing: first));
    }

    [TestMethod]
    public void A_skipped_next_date_moves_the_next_one_to_the_date_after_it() => AreEqual(
        // Halloween 2027 was cancelled, so the next Occurrence is Halloween 2028.
        ["2028-10-31T19:00:00-05:00"],
        Generate(RecurrenceRule.AnnualHoliday(HolidayPreset.Halloween), first: "2026-10-31T19:00:00-05:00",
            now: "2027-08-22T19:00:00-05:00", skipped: ["2027-10-31T19:00:00-05:00"]));

    [TestMethod]
    public void A_rule_date_exactly_at_now_is_not_the_next_one_after_now()
    {
        // At the very instant of Halloween 2027 the next one after now is Halloween 2028, 52 weeks out.
        AreEqual(
            ["2027-10-31T19:00:00-05:00", "2028-10-31T19:00:00-05:00"],
            Generate(RecurrenceRule.AnnualHoliday(HolidayPreset.Halloween), first: "2026-10-31T19:00:00-05:00",
                now: "2027-10-31T19:00:00-05:00"));
        AreEqual(
            ["2028-10-31T19:00:00-05:00"],
            Generate(RecurrenceRule.AnnualHoliday(HolidayPreset.Halloween), first: "2026-10-31T19:00:00-05:00",
                now: "2027-10-31T19:00:00-05:00", existing: ["2027-10-31T19:00:00-05:00"]));
    }

    [TestMethod]
    public void An_existing_next_date_far_out_is_enough() => AreEqual(
        [],
        Generate(RecurrenceRule.AnnualHoliday(HolidayPreset.Halloween), first: "2026-10-31T19:00:00-05:00",
            now: "2027-08-22T19:00:00-05:00", existing: ["2027-10-31T19:00:00-05:00"]));
}
