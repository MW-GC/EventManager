using Microsoft.Playwright;

namespace MW_GC.EventManager.E2ETests;

/// <summary>
/// The browser session's own plumbing (#193.2 and #193.11): the wait for the page to settle after an action, the
/// cleanup that always runs, and how a flow's result and a failed cleanup are reported. Only the start test needs a
/// browser and a stack; the others run anywhere.
/// </summary>
[TestClass]
public sealed class SessionPlumbingTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(300);

    [TestMethod]
    public async Task WaitForActionToSettle_WhenTheActionHasSentNoRequestYet_FailsNamingTheAction()
    {
        var traffic = new PageTraffic(Short);
        var mark = traffic.Mark();

        // Nothing in flight, so an idle-only wait would return at once; the action's first request is not here yet.
        var failure = await Assert.ThrowsExactlyAsync<AssertFailedException>(() => traffic.WaitForActionToSettleAsync(mark, "the test click"));
        Assert.Contains("sent no request for the test click", failure.Message);
    }

    [TestMethod]
    public async Task WaitForActionToSettle_ReturnsOnlyOnceTheActionsRequestHasEnded()
    {
        var traffic = new PageTraffic(TimeSpan.FromSeconds(30));
        var mark = traffic.Mark();
        var request = new object();

        traffic.Started(request);
        var wait = traffic.WaitForActionToSettleAsync(mark, "the test click");
        Assert.IsFalse(wait.IsCompleted, "The wait returned while the action's request was still in flight.");

        traffic.Ended(request);
        await wait;
    }

    [TestMethod]
    public async Task Cleanup_RunsEveryStepNewestFirst_EvenWhenOneFails()
    {
        var cleanups = new CleanupStack();
        var ran = new List<string>();
        cleanups.Push("first", () => { ran.Add("first"); return Task.CompletedTask; });
        cleanups.Push("second", () => { ran.Add("second"); throw new InvalidOperationException("boom"); });
        cleanups.Push("third", () => { ran.Add("third"); return Task.CompletedTask; });

        var failures = await cleanups.RunAllAsync();

        CollectionAssert.AreEqual(new[] { "third", "second", "first" }, ran);
        Assert.Contains("Cleanup of second failed: boom", Assert.ContainsSingle(failures).Message);
    }

    [TestMethod]
    public void Outcome_FlowAndCleanupBothFailed_KeepsTheFlowsFailureFirst()
    {
        var flow = new AssertFailedException("the flow's own failure");
        var cleanup = new InvalidOperationException("Cleanup of Game failed");

        var reported = Assert.ThrowsExactly<AggregateException>(() => FlowOutcome.Report(flow, [cleanup]));

        Assert.AreSame(flow, reported.InnerExceptions[0]);
        Assert.AreSame(cleanup, reported.InnerExceptions[1]);
        Assert.StartsWith("The flow failed: the flow's own failure", reported.Message);
    }

    [TestMethod]
    public void Outcome_InconclusiveFlow_IsNeverWrappedInAnAggregate()
    {
        var inconclusive = new AssertInconclusiveException("no stack");

        Assert.AreSame(inconclusive, Assert.ThrowsExactly<AssertInconclusiveException>(() => FlowOutcome.Report(inconclusive, [])));
        var withCleanupFailure = Assert.ThrowsExactly<AssertFailedException>(
            () => FlowOutcome.Report(inconclusive, [new InvalidOperationException("Cleanup of Game failed")]));
        Assert.AreSame(inconclusive, withCleanupFailure.InnerException);
        Assert.Contains("Cleanup of Game failed", withCleanupFailure.Message);
    }

    [TestMethod]
    public async Task Start_WhenSignInThrows_ClosesTheBrowserItStarted()
    {
        var appUrl = E2EEnvironment.AppUrlOrSkip();
        IBrowser? started = null;
        var signInFailure = new InvalidOperationException("sign-in failed");

        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => BrowserSession.StartAsync(appUrl, (context, _) =>
        {
            started = context.Browser;
            throw signInFailure;
        }));

        Assert.AreSame(signInFailure, thrown);
        Assert.IsNotNull(started, "The start never reached the sign-in.");
        Assert.IsFalse(started.IsConnected, "A failed start left its browser running.");
    }
}
