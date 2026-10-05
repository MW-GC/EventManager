using MW_GC.EventManager.Web.Shared;

namespace MW_GC.EventManager.Web.Tests;

// DialogSession.CloseAfterKeyAsync (#105): a clean dialog dismissed by Escape closes a moment later,
// so a select in it can finish with the same key first.
[TestClass]
public sealed class DialogSessionTests
{
    [TestMethod]
    public async Task CloseAfterKey_ClosesTheSameSession()
    {
        var session = new DialogSession();
        session.Open();
        var closed = 0;

        await session.CloseAfterKeyAsync(() => closed++);

        Assert.AreEqual(1, closed);
    }

    // A dialog closed (Cancel, a save) and opened again meanwhile is left open.
    [TestMethod]
    public async Task CloseAfterKey_LeavesANewerSessionOpen()
    {
        var session = new DialogSession();
        session.Open();
        var closed = 0;

        var closing = session.CloseAfterKeyAsync(() => closed++);
        session.Close();
        session.Open();
        await closing;

        Assert.AreEqual(0, closed);
    }

    // The close does not run inside the dismiss itself, but after the key's work.
    [TestMethod]
    public async Task CloseAfterKey_DoesNotCloseAtOnce()
    {
        var session = new DialogSession();
        session.Open();
        var closed = 0;

        var closing = session.CloseAfterKeyAsync(() => closed++);
        Assert.AreEqual(0, closed);
        await closing;

        Assert.AreEqual(1, closed);
    }
}
