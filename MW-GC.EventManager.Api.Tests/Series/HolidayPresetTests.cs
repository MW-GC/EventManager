using MW_GC.EventManager.Shared.Series;
using static MW_GC.EventManager.Api.Tests.Series.SeriesTestSupport;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// The in-house holiday table (#204). Every expected date is written out by hand from published calendars,
/// never computed with the table's own formula. Easter is the Gregorian Easter Sunday; Thanksgiving 2027 is
/// 25 November, as the Nager.Date public holiday API gives it.
/// </summary>
[TestClass]
public sealed class HolidayPresetTests
{
    [TestMethod]
    [DataRow(2000, "2000-04-23")]
    [DataRow(2008, "2008-03-23")]
    [DataRow(2011, "2011-04-24")]
    [DataRow(2016, "2016-03-27")]
    [DataRow(2019, "2019-04-21")]
    [DataRow(2024, "2024-03-31")]
    [DataRow(2025, "2025-04-20")]
    [DataRow(2026, "2026-04-05")]
    [DataRow(2027, "2027-03-28")]
    [DataRow(2028, "2028-04-16")]
    [DataRow(2030, "2030-04-21")]
    [DataRow(2035, "2035-03-25")]
    [DataRow(2038, "2038-04-25")]
    [DataRow(2049, "2049-04-18")]
    public void Easter_Sunday(int year, string expected) =>
        Assert.AreEqual(DateOnly.Parse(expected), HolidayPresets.DateIn(HolidayPreset.EasterSunday, year));

    [TestMethod]
    [DataRow(HolidayPreset.MartinLutherKingJrDay, "2026-01-19", "2027-01-18")]
    [DataRow(HolidayPreset.PresidentsDay, "2026-02-16", "2027-02-15")]
    [DataRow(HolidayPreset.MothersDay, "2026-05-10", "2027-05-09")]
    [DataRow(HolidayPreset.MemorialDay, "2026-05-25", "2027-05-31")]
    [DataRow(HolidayPreset.FathersDay, "2026-06-21", "2027-06-20")]
    [DataRow(HolidayPreset.LaborDay, "2026-09-07", "2027-09-06")]
    [DataRow(HolidayPreset.ColumbusDay, "2026-10-12", "2027-10-11")]
    [DataRow(HolidayPreset.Thanksgiving, "2026-11-26", "2027-11-25")]
    public void Floating_holiday(HolidayPreset preset, string in2026, string in2027)
    {
        Assert.AreEqual(DateOnly.Parse(in2026), HolidayPresets.DateIn(preset, 2026), $"{preset} 2026");
        Assert.AreEqual(DateOnly.Parse(in2027), HolidayPresets.DateIn(preset, 2027), $"{preset} 2027");
    }

    [TestMethod]
    [DataRow(HolidayPreset.NewYearsDay, "2027-01-01")]
    [DataRow(HolidayPreset.ValentinesDay, "2027-02-14")]
    [DataRow(HolidayPreset.StPatricksDay, "2027-03-17")]
    [DataRow(HolidayPreset.EarthDay, "2027-04-22")]
    [DataRow(HolidayPreset.IndependenceDay, "2027-07-04")]
    [DataRow(HolidayPreset.Halloween, "2027-10-31")]
    [DataRow(HolidayPreset.ChristmasEve, "2027-12-24")]
    [DataRow(HolidayPreset.Christmas, "2027-12-25")]
    [DataRow(HolidayPreset.NewYearsEve, "2027-12-31")]
    public void Fixed_date_holiday(HolidayPreset preset, string expected) =>
        Assert.AreEqual(DateOnly.Parse(expected), HolidayPresets.DateIn(preset, 2027));

    [TestMethod]
    public void Every_preset_has_a_row_in_the_table()
    {
        foreach (var preset in Enum.GetValues<HolidayPreset>())
            Assert.AreEqual(2027, HolidayPresets.DateIn(preset, 2027).Year, preset.ToString());
    }

    [TestMethod]
    public void An_undefined_preset_has_no_date() =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => HolidayPresets.DateIn((HolidayPreset)99, 2027));

    [TestMethod]
    public void A_Thanksgiving_Series_lands_on_the_fourth_Thursday_at_the_first_Occurrence_time_of_day() => AreEqual(
        // Now is after Thanksgiving 2026, so only the next one is made, 52 weeks out.
        ["2027-11-25T17:30:00-05:00"],
        Generate(RecurrenceRule.AnnualHoliday(HolidayPreset.Thanksgiving),
            first: "2026-11-26T17:30:00-05:00", now: "2026-11-27T09:00:00-05:00"));

    [TestMethod]
    public void An_Easter_Series_moves_with_Easter_year_by_year()
    {
        AreEqual(
            ["2027-03-28T15:00:00+00:00"],
            Generate(RecurrenceRule.AnnualHoliday(HolidayPreset.EasterSunday),
                first: "2026-04-05T15:00:00+00:00", now: "2026-04-06T09:00:00+00:00"));
        AreEqual(
            ["2028-04-16T15:00:00+00:00"],
            Generate(RecurrenceRule.AnnualHoliday(HolidayPreset.EasterSunday),
                first: "2026-04-05T15:00:00+00:00", now: "2027-03-29T09:00:00+00:00"));
    }

    [TestMethod]
    public void A_holiday_Series_started_before_the_holiday_begins_with_that_year() => AreEqual(
        // The first instant picks the year and the time of day; the date comes from the preset.
        ["2027-10-31T19:00:00-05:00"],
        Generate(RecurrenceRule.AnnualHoliday(HolidayPreset.Halloween),
            first: "2027-01-10T19:00:00-05:00", now: "2027-01-10T09:00:00-05:00"));
}
