using MW_GC.EventManager.Shared.Series;
using static MW_GC.EventManager.Api.Tests.Series.SeriesTestSupport;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// <see cref="RecurrenceRule.Validate"/> names the first problem with a rule, or answers null; the generator
/// refuses an invalid rule with that message (#204).
/// </summary>
[TestClass]
public sealed class RecurrenceRuleValidationTests
{
    public static IEnumerable<object[]> ValidRules =>
    [
        [RecurrenceRule.Weekly()],
        [RecurrenceRule.BiWeekly()],
        [RecurrenceRule.MonthlyNthWeekday(WeekOfMonth.First, DayOfWeek.Sunday)],
        [RecurrenceRule.MonthlyNthWeekday(WeekOfMonth.Fourth, DayOfWeek.Saturday)],
        [RecurrenceRule.MonthlyNthWeekday(WeekOfMonth.Last, DayOfWeek.Friday)],
        [RecurrenceRule.MonthlySameDate()],
        [RecurrenceRule.AnnualSameDate()],
        [RecurrenceRule.AnnualHoliday(HolidayPreset.NewYearsDay)],
        [RecurrenceRule.AnnualHoliday(HolidayPreset.NewYearsEve)],
    ];

    public static IEnumerable<object[]> InvalidRules =>
    [
        [RecurrenceRule.MonthlyNthWeekday((WeekOfMonth)5, DayOfWeek.Friday), "Week of month must be First, Second, Third, Fourth or Last."],
        [RecurrenceRule.MonthlyNthWeekday((WeekOfMonth)0, DayOfWeek.Friday), "Week of month must be First, Second, Third, Fourth or Last."],
        [new RecurrenceRule(RecurrenceKind.MonthlyNthWeekday, Weekday: DayOfWeek.Friday), "Week of month must be First, Second, Third, Fourth or Last."],
        [RecurrenceRule.MonthlyNthWeekday(WeekOfMonth.Second, (DayOfWeek)7), "Weekday must be a day of the week."],
        [new RecurrenceRule(RecurrenceKind.MonthlyNthWeekday, WeekOfMonth.Second), "Weekday must be a day of the week."],
        [RecurrenceRule.AnnualHoliday((HolidayPreset)99), "Holiday preset must be a known preset."],
        [new RecurrenceRule(RecurrenceKind.AnnualHoliday), "Holiday preset must be a known preset."],
        [new RecurrenceRule((RecurrenceKind)42), "Repeat must be a known rule."],
    ];

    [TestMethod]
    [DynamicData(nameof(ValidRules))]
    public void A_valid_rule_has_no_message(RecurrenceRule rule) => Assert.IsNull(rule.Validate());

    [TestMethod]
    [DynamicData(nameof(InvalidRules))]
    public void An_invalid_rule_is_named(RecurrenceRule rule, string message) =>
        Assert.AreEqual(message, rule.Validate());

    [TestMethod]
    [DynamicData(nameof(InvalidRules))]
    public void The_generator_refuses_an_invalid_rule_with_its_message(RecurrenceRule rule, string message)
    {
        var refused = Assert.ThrowsExactly<ArgumentException>(() => OccurrenceGenerator.Generate(
            new RecurrenceSeries(At("2027-03-04T19:00:00-05:00"), rule), At("2027-03-04T12:00:00-05:00"), [], []));
        Assert.IsTrue(refused.Message.StartsWith(message, StringComparison.Ordinal), refused.Message);
    }
}
