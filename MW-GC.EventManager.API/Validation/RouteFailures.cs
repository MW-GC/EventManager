using Azure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MW_GC.EventManager.API.Services;

namespace MW_GC.EventManager.API.Validation;

/// <summary>
/// Runs a route and maps a failure that escapes it to a status code with a short, generic body
/// (never a stack trace or exception text). Every failure is logged through <see cref="ILogger"/>,
/// so a storage outage no longer looks the same as bad input.
/// </summary>
internal static class RouteFailures
{
    public const string ConflictMessage = "The request conflicts with the stored data. Reload and try again.";
    public const string TooLargeMessage = "The item is too large to store.";
    public const string UnavailableMessage = "Storage is temporarily unavailable. Try again shortly.";
    public const string UnexpectedMessage = "An unexpected error occurred.";

    // Table Storage reports an oversized entity or property as a 400 with one of these codes.
    private static readonly HashSet<string> TooLargeCodes =
        new(StringComparer.Ordinal) { "EntityTooLarge", "PropertyValueTooLarge", "RequestBodyTooLarge" };

    public static async Task<IActionResult> RunAsync(
        ILogger logger, string operation, CancellationToken ct, Func<Task<IActionResult>> route)
    {
        try
        {
            return await route();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller went away; there is nobody to answer and nothing failed on our side.
            throw;
        }
        // Storage answers a failed If-Match with 412; to the caller that is the same stale-data 409.
        catch (RequestFailedException ex) when (ex.Status is StatusCodes.Status409Conflict or StatusCodes.Status412PreconditionFailed)
        {
            logger.LogWarning(ex, "{Operation} hit a storage conflict ({ErrorCode}).", operation, ex.ErrorCode);
            return Result(StatusCodes.Status409Conflict, ConflictMessage);
        }
        catch (StoredValueTooLargeException ex)
        {
            // Refused before any write: the row would pass Table Storage's 1 MiB or 252-property limit.
            logger.LogWarning(ex, "{Operation} was refused as too large to store ({Property}).", operation, ex.Property);
            return Result(StatusCodes.Status413PayloadTooLarge, ex.Message);
        }
        catch (RequestFailedException ex) when (IsTooLarge(ex))
        {
            logger.LogWarning(ex, "{Operation} was refused by storage as too large ({ErrorCode}).", operation, ex.ErrorCode);
            return Result(StatusCodes.Status413PayloadTooLarge, TooLargeMessage);
        }
        catch (Exception ex) when (IsOutage(ex))
        {
            logger.LogError(ex, "{Operation} failed because storage is unavailable.", operation);
            return Result(StatusCodes.Status503ServiceUnavailable, UnavailableMessage);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Operation} failed unexpectedly.", operation);
            return Result(StatusCodes.Status500InternalServerError, UnexpectedMessage);
        }
    }

    private static bool IsTooLarge(RequestFailedException ex) =>
        ex.Status == StatusCodes.Status413PayloadTooLarge
        || (ex.ErrorCode is not null && TooLargeCodes.Contains(ex.ErrorCode));

    // A storage-side 5xx, a transport failure (Azure.Core reports one as status 0), or a
    // timeout that is not the request's own cancellation (that case is rethrown above).
    // Azure.Core wraps the attempts in an AggregateException once its retries run out.
    internal static bool IsOutage(Exception ex) => ex switch
    {
        RequestFailedException failed => failed.Status >= StatusCodes.Status500InternalServerError || failed.Status == 0,
        HttpRequestException => true,
        TaskCanceledException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsOutage),
        _ => false,
    };

    private static ObjectResult Result(int status, string message) => new(message) { StatusCode = status };
}
