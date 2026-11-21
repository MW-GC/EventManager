namespace MW_GC.EventManager.E2ETests;

[TestClass]
public sealed class SmokeTests
{
    [TestMethod]
    public Task SignedInAdmin_OpensEventsPage_SeesItsHeading() =>
        BrowserSession.RunAsync(session => session.OpenEventsPageAsync());
}
