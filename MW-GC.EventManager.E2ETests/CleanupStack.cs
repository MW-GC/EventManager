namespace MW_GC.EventManager.E2ETests;

/// <summary>
/// The steps that undo what a browser flow set up: its seeded data, its held requests, and the browser itself. They
/// run newest first, so an Event goes before its Game and the browser closes last, and every step runs even when an
/// earlier one failed.
/// </summary>
internal sealed class CleanupStack
{
    private readonly Stack<(string What, Func<Task> Step)> _steps = new();

    public void Push(string what, Func<Task> step) => _steps.Push((what, step));

    /// <summary>Runs every step, newest first, and returns what failed (empty when all succeeded).</summary>
    public async Task<List<Exception>> RunAllAsync()
    {
        var failures = new List<Exception>();
        while (_steps.TryPop(out var cleanup))
        {
            try
            {
                await cleanup.Step();
            }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException($"Cleanup of {cleanup.What} failed: {ex.Message}", ex));
            }
        }
        return failures;
    }
}
