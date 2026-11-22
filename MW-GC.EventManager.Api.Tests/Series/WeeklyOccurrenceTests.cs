using MW_GC.EventManager.Shared.Series;
using static MW_GC.EventManager.Api.Tests.Series.SeriesTestSupport;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// Weekly and every-other-week Series (#204). An Occurrence is an instant: the rule adds whole days at the
/// first Occurrence's UTC offset and never reads a time zone, so the UTC time stays put across both daylight
/// saving changes of 2027 (US: 14 March and 7 November; UK: 28 March and 31 October) and the offset in every
/// answer is the Series' own.
/// </summary>
[TestClass]
public sealed class WeeklyOccurrenceTests
{
    [TestMethod]
    public void Weekly_at_minus_five_keeps_its_UTC_time_across_the_March_change() => AreEqual(
        [
            "2027-03-04T19:00:00-05:00", "2027-03-11T19:00:00-05:00", "2027-03-18T19:00:00-05:00",
            "2027-03-25T19:00:00-05:00", "2027-04-01T19:00:00-05:00", "2027-04-08T19:00:00-05:00",
            "2027-04-15T19:00:00-05:00", "2027-04-22T19:00:00-05:00",
        ],
        Generate(RecurrenceRule.Weekly(), first: "2027-03-04T19:00:00-05:00", now: "2027-03-04T12:00:00-05:00"));

    [TestMethod]
    public void Weekly_at_minus_five_keeps_its_UTC_time_across_the_November_change() => AreEqual(
        [
            "2027-10-21T19:00:00-05:00", "2027-10-28T19:00:00-05:00", "2027-11-04T19:00:00-05:00",
            "2027-11-11T19:00:00-05:00", "2027-11-18T19:00:00-05:00", "2027-11-25T19:00:00-05:00",
            "2027-12-02T19:00:00-05:00", "2027-12-09T19:00:00-05:00",
        ],
        Generate(RecurrenceRule.Weekly(), first: "2027-03-04T19:00:00-05:00", now: "2027-10-21T12:00:00-05:00"));

    [TestMethod]
    public void Weekly_at_UTC_keeps_its_time_across_the_UK_March_change() => AreEqual(
        [
            "2027-03-18T20:00:00+00:00", "2027-03-25T20:00:00+00:00", "2027-04-01T20:00:00+00:00",
            "2027-04-08T20:00:00+00:00", "2027-04-15T20:00:00+00:00", "2027-04-22T20:00:00+00:00",
            "2027-04-29T20:00:00+00:00", "2027-05-06T20:00:00+00:00",
        ],
        Generate(RecurrenceRule.Weekly(), first: "2027-03-18T20:00:00+00:00", now: "2027-03-18T08:00:00+00:00"));

    [TestMethod]
    public void Weekly_at_UTC_keeps_its_time_across_the_UK_October_change() => AreEqual(
        [
            "2027-10-21T20:00:00+00:00", "2027-10-28T20:00:00+00:00", "2027-11-04T20:00:00+00:00",
            "2027-11-11T20:00:00+00:00", "2027-11-18T20:00:00+00:00", "2027-11-25T20:00:00+00:00",
            "2027-12-02T20:00:00+00:00", "2027-12-09T20:00:00+00:00",
        ],
        Generate(RecurrenceRule.Weekly(), first: "2027-03-18T20:00:00+00:00", now: "2027-10-21T08:00:00+00:00"));

    [TestMethod]
    public void Every_other_week_at_minus_five_keeps_its_UTC_time_across_the_March_change() => AreEqual(
        [
            "2027-03-04T19:00:00-05:00", "2027-03-18T19:00:00-05:00",
            "2027-04-01T19:00:00-05:00", "2027-04-15T19:00:00-05:00",
        ],
        Generate(RecurrenceRule.BiWeekly(), first: "2027-03-04T19:00:00-05:00", now: "2027-03-04T12:00:00-05:00"));

    [TestMethod]
    public void Every_other_week_at_minus_five_stays_in_step_with_its_first_Occurrence_in_November() => AreEqual(
        [
            // 21 October is 33 weeks after 4 March, an odd week, so the cycle starts on the 28th.
            "2027-10-28T19:00:00-05:00", "2027-11-11T19:00:00-05:00",
            "2027-11-25T19:00:00-05:00", "2027-12-09T19:00:00-05:00",
        ],
        Generate(RecurrenceRule.BiWeekly(), first: "2027-03-04T19:00:00-05:00", now: "2027-10-21T12:00:00-05:00"));

    [TestMethod]
    public void Every_other_week_at_UTC_keeps_its_time_across_both_UK_changes()
    {
        AreEqual(
            [
                "2027-03-18T20:00:00+00:00", "2027-04-01T20:00:00+00:00",
                "2027-04-15T20:00:00+00:00", "2027-04-29T20:00:00+00:00",
            ],
            Generate(RecurrenceRule.BiWeekly(), first: "2027-03-18T20:00:00+00:00", now: "2027-03-18T08:00:00+00:00"));
        AreEqual(
            [
                // 21 October is 31 weeks after 18 March: odd, so the cycle starts on the 28th.
                "2027-10-28T20:00:00+00:00", "2027-11-11T20:00:00+00:00",
                "2027-11-25T20:00:00+00:00", "2027-12-09T20:00:00+00:00",
            ],
            Generate(RecurrenceRule.BiWeekly(), first: "2027-03-18T20:00:00+00:00", now: "2027-10-21T08:00:00+00:00"));
    }
}
