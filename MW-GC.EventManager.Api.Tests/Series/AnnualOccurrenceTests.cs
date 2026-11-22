using MW_GC.EventManager.Shared.Series;
using static MW_GC.EventManager.Api.Tests.Series.SeriesTestSupport;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>Yearly on the same date (#204): 29 February falls on 28 February outside leap years.</summary>
[TestClass]
public sealed class AnnualOccurrenceTests
{
    [TestMethod]
    public void The_same_date_repeats_every_year() => AreEqual(
        ["2028-07-04T20:00:00-05:00"],
        Generate(RecurrenceRule.AnnualSameDate(), first: "2027-07-04T20:00:00-05:00", now: "2027-07-05T09:00:00-05:00"));

    [TestMethod]
    public void February_29_falls_on_February_28_in_a_year_without_one()
    {
        AreEqual(
            ["2029-02-28T19:00:00-05:00"],
            Generate(RecurrenceRule.AnnualSameDate(), first: "2028-02-29T19:00:00-05:00", now: "2028-03-01T09:00:00-05:00"));
        AreEqual(
            ["2031-02-28T19:00:00-05:00"],
            Generate(RecurrenceRule.AnnualSameDate(), first: "2028-02-29T19:00:00-05:00", now: "2030-03-01T09:00:00-05:00"));
    }

    [TestMethod]
    public void February_29_comes_back_in_the_next_leap_year() => AreEqual(
        ["2032-02-29T19:00:00-05:00"],
        Generate(RecurrenceRule.AnnualSameDate(), first: "2028-02-29T19:00:00-05:00", now: "2031-03-01T09:00:00-05:00"));
}
