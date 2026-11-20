using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MW_GC.EventManager.API.Services;

/// <summary>
/// Runs <see cref="ActivityNoteConversion"/> once each time the Function App host starts, so the
/// conversion needs no manual step. It runs in the background: the host does not wait for it,
/// and a failure is logged and never stops the host. Until the marker is stored, every Activity
/// write that replaces a row also runs the pass first (<see cref="ActivityNoteConversion.EnsureConvertedAsync"/>),
/// waiting for this one when it is still running, so a request served before this pass finishes,
/// or after it failed, never drops an old text. The next start or Activity write retries an
/// unfinished pass. The conversion is resolved inside the guard, not injected, because building
/// it builds the table stores, which create their tables and can fail when storage or its auth is down.
/// </summary>
internal sealed class ActivityNoteConversionStartup(
    IServiceProvider services,
    ILogger<ActivityNoteConversionStartup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var conversion = services.GetRequiredService<ActivityNoteConversion>();
            await conversion.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping; the next start finishes the pass.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Converting old Activity texts into Comments failed; the next host start or Activity write retries it.");
        }
    }
}
