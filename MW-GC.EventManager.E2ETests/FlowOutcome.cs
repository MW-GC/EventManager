using System.Runtime.ExceptionServices;

namespace MW_GC.EventManager.E2ETests;

/// <summary>
/// How a browser flow ends once its cleanup has run. Cleanup never hides the flow's result and never disappears
/// behind it: the flow's own exception stays first, and an inconclusive flow is never wrapped in an
/// <see cref="AggregateException"/>.
/// </summary>
internal static class FlowOutcome
{
    public static void Report(Exception? flowFailure, IReadOnlyList<Exception> cleanupFailures)
    {
        if (cleanupFailures.Count == 0)
        {
            if (flowFailure is not null) ExceptionDispatchInfo.Throw(flowFailure);
            return;
        }

        var cleanup = string.Join(" | ", cleanupFailures.Select(f => f.Message));
        switch (flowFailure)
        {
            case null:
                throw new AssertFailedException($"Cleanup failed: {cleanup}", new AggregateException(cleanupFailures));
            // A flow that could not tell is not a pass, but a failed cleanup is a real failure: fail, naming both.
            case AssertInconclusiveException inconclusive:
                throw new AssertFailedException(
                    $"The flow was inconclusive ({inconclusive.Message}), and its cleanup failed: {cleanup}", inconclusive);
            default:
                throw new AggregateException(
                    $"The flow failed: {flowFailure.Message} Its cleanup failed too: {cleanup}", [flowFailure, .. cleanupFailures]);
        }
    }
}
