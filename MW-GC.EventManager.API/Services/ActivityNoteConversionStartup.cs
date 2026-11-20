using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MW_GC.EventManager.API.Services;

/// <summary>
/// Runs <see cref="ActivityNoteConversion"/> once each time the Function App host starts, so the
/// conversion needs no manual step. It runs in the background: the host does not wait for it,
/// and a failure is logged and never stops the host. The next start retries an unfinished pass.
/// </summary>
internal sealed class ActivityNoteConversionStartup(
    ActivityNoteConversion conversion,
    ILogger<ActivityNoteConversionStartup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await conversion.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping; the next start finishes the pass.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Converting old Activity notes into Comments failed; the next host start retries it.");
        }
    }
}
