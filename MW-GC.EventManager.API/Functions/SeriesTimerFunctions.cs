using Microsoft.Azure.Functions.Worker;
using MW_GC.EventManager.API.Services;

namespace MW_GC.EventManager.API.Functions;

/// <summary>
/// The daily Series run (#204, ADR 0002): a timer trigger with no HTTP surface, so the anonymous HTTP
/// triggers and the Static Web App link are unchanged. All the work is in <see cref="SeriesExtender"/>.
/// </summary>
internal sealed class SeriesTimerFunctions(SeriesExtender extender)
{
    /// <summary>
    /// NCRONTAB (second, minute, hour, day, month, day of week): every day at 04:00. The host reads it in UTC,
    /// since the app sets no <c>WEBSITE_TIME_ZONE</c>.
    /// </summary>
    public const string Schedule = "0 0 4 * * *";

    /// <summary>The clock the run reads "now" from. Tests set a fixed one.</summary>
    internal TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>
    /// Extends every Series. Not run when the host starts: a run on every restart or scale-out would add
    /// nothing the daily run does not, and a new Series generates its own Occurrences when it is created.
    /// </summary>
    [Function("ExtendSeries")]
    public Task Run([TimerTrigger(Schedule, RunOnStartup = false)] TimerInfo timer, CancellationToken ct) =>
        extender.ExtendAllAsync(Clock, ct);
}
