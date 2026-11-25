using System.Reflection;
using Microsoft.Azure.Functions.Worker;
using MW_GC.EventManager.API.Functions;
using static MW_GC.EventManager.Api.Tests.Series.SeriesExtenderSupport;
using static MW_GC.EventManager.Api.Tests.Series.SeriesHarness;
using static MW_GC.EventManager.Api.Tests.Series.SeriesTestSupport;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// The timer function (#204): once a day at 04:00 UTC, never when the host starts, and all it does is run the
/// Series extension with its clock.
/// </summary>
[TestClass]
public sealed class SeriesTimerFunctionTests
{
    private static MethodInfo Run() =>
        typeof(SeriesTimerFunctions).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m => m.GetCustomAttribute<FunctionAttribute>() is not null);

    private static TimerTriggerAttribute Trigger() =>
        Run().GetParameters().Select(p => p.GetCustomAttribute<TimerTriggerAttribute>()).Single(a => a is not null)!;

    [TestMethod]
    public void It_runs_once_a_day_at_four_in_the_morning_UTC()
    {
        // NCRONTAB: second, minute, hour, day, month, day of week. The host reads it in UTC unless
        // WEBSITE_TIME_ZONE is set, which this app never sets.
        Assert.AreEqual("0 0 4 * * *", Trigger().Schedule);
        Assert.AreEqual("ExtendSeries", Run().GetCustomAttribute<FunctionAttribute>()!.Name);
    }

    [TestMethod]
    public void It_does_not_run_when_the_host_starts()
    {
        Assert.IsFalse(Trigger().RunOnStartup);
    }

    [TestMethod]
    public void It_is_the_only_function_of_its_class_and_has_no_HTTP_trigger()
    {
        var functions = typeof(SeriesTimerFunctions).GetMethods().Where(m => m.GetCustomAttribute<FunctionAttribute>() is not null).ToList();
        Assert.HasCount(1, functions);
        Assert.IsFalse(functions[0].GetParameters().Any(p => p.GetCustomAttribute<HttpTriggerAttribute>() is not null));
    }

    [TestMethod]
    public async Task A_run_extends_every_Series_from_its_clock()
    {
        var h = new SeriesHarness();
        var (id, _, _) = await h.CreateAsync();
        var function = new SeriesTimerFunctions(Extender(h)) { Clock = new FixedClock(At("2027-03-12T12:00:00-05:00")) };

        await function.Run(new TimerInfo(), default);

        Assert.AreEqual("2027-05-06T19:00:00-05:00", Dates(await h.OccurrencesAsync(id)).Last());
        Assert.HasCount(10, await h.OccurrencesAsync(id));
    }
}
