using MW_GC.EventManager.Shared.Series;
using MW_GC.EventManager.Web.Shared.Events;

namespace MW_GC.EventManager.Web.Tests;

// #100, #204: the Repeat section's pure model. Every expected value is written out by hand: 10 December 2026 is a
// Thursday in the second week of its month, 31 December 2026 a Thursday in its last, and Thanksgiving 2026 is 26
// November (the fourth Thursday).
[TestClass]
public sealed class RepeatChoiceTests
{
    private static readonly TimeSpan Chicago = TimeSpan.FromHours(-6);
    private static readonly DateTimeOffset SecondThursday = new(2026, 12, 10, 19, 0, 0, Chicago);

    [TestMethod]
    public void ANewChoice_IsOff_AndWeekly_WithNoEnd()
    {
        var choice = new RepeatChoice();

        Assert.IsFalse(choice.IsOn);
        Assert.AreEqual(RecurrenceKind.Weekly, choice.Kind);
        Assert.IsNull(choice.EndDate);
        Assert.IsNull(choice.EndFor(SecondThursday));
        Assert.IsNull(choice.ScheduleFor(SecondThursday).End);
    }

    [TestMethod]
    public void TheRulesAreOffered_InTheseWords_WeeklyFirst()
    {
        CollectionAssert.AreEqual(
            new[] { "Weekly", "Every other week", "Monthly on the nth weekday", "Monthly on the same date", "Yearly on the same date", "Yearly on a holiday" },
            RepeatChoice.Rules.Select(r => r.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "First", "Second", "Third", "Fourth", "Last" }, RepeatChoice.Weeks.Select(w => w.Name).ToArray());
    }

    [TestMethod]
    [DataRow(RecurrenceKind.Weekly)]
    [DataRow(RecurrenceKind.BiWeekly)]
    [DataRow(RecurrenceKind.MonthlyNthWeekday)]
    [DataRow(RecurrenceKind.MonthlySameDate)]
    [DataRow(RecurrenceKind.AnnualSameDate)]
    [DataRow(RecurrenceKind.AnnualHoliday)]
    public void EachRule_MakesItsExactRecurrenceRule(RecurrenceKind kind)
    {
        var choice = new RepeatChoice { IsOn = true, Kind = kind };

        var expected = kind switch
        {
            RecurrenceKind.Weekly => new RecurrenceRule(RecurrenceKind.Weekly),
            RecurrenceKind.BiWeekly => new RecurrenceRule(RecurrenceKind.BiWeekly),
            RecurrenceKind.MonthlyNthWeekday => new RecurrenceRule(RecurrenceKind.MonthlyNthWeekday, WeekOfMonth.Second, DayOfWeek.Thursday),
            RecurrenceKind.MonthlySameDate => new RecurrenceRule(RecurrenceKind.MonthlySameDate),
            RecurrenceKind.AnnualSameDate => new RecurrenceRule(RecurrenceKind.AnnualSameDate),
            _ => new RecurrenceRule(RecurrenceKind.AnnualHoliday, Preset: HolidayPreset.NewYearsDay),
        };
        Assert.AreEqual(expected, choice.ScheduleFor(SecondThursday).Rule);
    }

    // A Thursday in the second week gives Second and Thursday.
    [TestMethod]
    public void TheNthWeekday_DefaultsToThePickedDatesWeekAndWeekday()
    {
        var choice = new RepeatChoice { IsOn = true, Kind = RecurrenceKind.MonthlyNthWeekday };

        Assert.AreEqual(RecurrenceRule.MonthlyNthWeekday(WeekOfMonth.Second, DayOfWeek.Thursday), choice.ScheduleFor(SecondThursday).Rule);
    }

    [TestMethod]
    [DataRow(1, WeekOfMonth.First)]
    [DataRow(7, WeekOfMonth.First)]
    [DataRow(8, WeekOfMonth.Second)]
    [DataRow(14, WeekOfMonth.Second)]
    [DataRow(15, WeekOfMonth.Third)]
    [DataRow(21, WeekOfMonth.Third)]
    [DataRow(22, WeekOfMonth.Fourth)]
    [DataRow(28, WeekOfMonth.Fourth)]
    [DataRow(29, WeekOfMonth.Last)]
    [DataRow(31, WeekOfMonth.Last)]
    public void TheDefaultWeek_FollowsTheDay(int day, WeekOfMonth week) =>
        Assert.AreEqual(week, RepeatChoice.DefaultWeek(new DateOnly(2026, 12, day)));

    // A date in the last week (a fifth Thursday) is Last, never a Fourth one week too early.
    [TestMethod]
    public void ADateInTheLastWeek_GivesLast()
    {
        var choice = new RepeatChoice { IsOn = true, Kind = RecurrenceKind.MonthlyNthWeekday };

        Assert.AreEqual(RecurrenceRule.MonthlyNthWeekday(WeekOfMonth.Last, DayOfWeek.Thursday),
            choice.ScheduleFor(new DateTimeOffset(2026, 12, 31, 19, 0, 0, Chicago)).Rule);
    }

    [TestMethod]
    public void AChosenWeekAndWeekday_WinOverTheDate()
    {
        var choice = new RepeatChoice { IsOn = true, Kind = RecurrenceKind.MonthlyNthWeekday, ChosenWeek = WeekOfMonth.Last, ChosenWeekday = DayOfWeek.Friday };

        Assert.AreEqual(RecurrenceRule.MonthlyNthWeekday(WeekOfMonth.Last, DayOfWeek.Friday), choice.ScheduleFor(SecondThursday).Rule);
    }

    // 23:30 on Thursday at UTC-06:00 is already Friday in UTC: the weekday is read at the start's own offset.
    [TestMethod]
    public void TheWeekday_IsReadAtTheStartsOwnOffset()
    {
        var choice = new RepeatChoice { IsOn = true, Kind = RecurrenceKind.MonthlyNthWeekday };
        var late = new DateTimeOffset(2026, 12, 10, 23, 30, 0, Chicago);

        Assert.AreEqual(RecurrenceRule.MonthlyNthWeekday(WeekOfMonth.Second, DayOfWeek.Thursday), choice.ScheduleFor(late).Rule);
    }

    [TestMethod]
    public void TheHoliday_DefaultsToTheOneOnThePickedDate_AndAChosenOneWins()
    {
        var thanksgiving = new DateTimeOffset(2026, 11, 26, 18, 0, 0, Chicago);
        var choice = new RepeatChoice { IsOn = true, Kind = RecurrenceKind.AnnualHoliday };

        Assert.AreEqual(RecurrenceRule.AnnualHoliday(HolidayPreset.Thanksgiving), choice.ScheduleFor(thanksgiving).Rule);
        choice.ChosenHolidayPreset = HolidayPreset.EasterSunday;
        Assert.AreEqual(RecurrenceRule.AnnualHoliday(HolidayPreset.EasterSunday), choice.ScheduleFor(thanksgiving).Rule);
    }

    // One table of names: every preset has exactly one readable name, and the names are distinct.
    [TestMethod]
    public void EveryHolidayPreset_HasOneReadableName()
    {
        foreach (var preset in Enum.GetValues<HolidayPreset>())
        {
            var name = Assert.ContainsSingle(RepeatChoice.HolidayPresetOptions.Where(h => h.Value == preset)).Name;
            Assert.IsFalse(string.IsNullOrWhiteSpace(name), preset.ToString());
        }
        Assert.HasCount(Enum.GetValues<HolidayPreset>().Length, RepeatChoice.HolidayPresetOptions);
        Assert.HasCount(RepeatChoice.HolidayPresetOptions.Count, RepeatChoice.HolidayPresetOptions.Select(h => h.Name).Distinct());
        Assert.AreEqual("Thanksgiving", RepeatChoice.HolidayPresetName(HolidayPreset.Thanksgiving));
        Assert.AreEqual("Martin Luther King Jr. Day", RepeatChoice.HolidayPresetName(HolidayPreset.MartinLutherKingJrDay));
        Assert.AreEqual("St. Patrick's Day", RepeatChoice.HolidayPresetName(HolidayPreset.StPatricksDay));
    }

    // The start is sent exactly as given, its offset kept, and the end is the end date at the start's time and offset.
    [TestMethod]
    public void TheSchedule_KeepsTheStartsOffset_AndPutsTheEndAtItsTimeAndOffset()
    {
        var choice = new RepeatChoice { IsOn = true, EndDate = new DateOnly(2027, 3, 4) };

        var schedule = choice.ScheduleFor(SecondThursday);

        Assert.AreEqual(SecondThursday, schedule.First);
        Assert.AreEqual(Chicago, schedule.First.Offset);
        Assert.AreEqual(new DateTimeOffset(2027, 3, 4, 19, 0, 0, Chicago), schedule.End);
        Assert.AreEqual(Chicago, schedule.End!.Value.Offset);
    }

    [TestMethod]
    [DataRow(2026, 12, 9, true)]
    [DataRow(2026, 12, 10, true)]
    [DataRow(2026, 12, 11, false)]
    public void AnEndNotAfterTheStart_GivesTheApisWording(int year, int month, int day, bool refused)
    {
        var choice = new RepeatChoice { IsOn = true, EndDate = new DateOnly(year, month, day) };

        Assert.AreEqual(refused ? "End must be after the start." : null, choice.EndProblem(SecondThursday));
    }

    [TestMethod]
    public void NoEnd_IsNoProblem() => Assert.IsNull(new RepeatChoice { IsOn = true }.EndProblem(SecondThursday));
}
