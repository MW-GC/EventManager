using static MW_GC.EventManager.E2ETests.BrowserSession;

namespace MW_GC.EventManager.E2ETests;

[TestClass]
public sealed class SmokeTests
{
    [TestMethod]
    public Task SignedInAdmin_OpensEventsPage_SeesItsHeading() =>
        RunAsync(session => session.OpenEventsPageAsync());
}
