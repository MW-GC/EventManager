using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Tests;

// #198.4: LoadErrors.LoadResultAsync on its own, as every page uses it (directly, or through LoadAsync): a read that
// succeeds, one that reports its failure, and one that throws. The pages' failure tests cover it only through them.
[TestClass]
public sealed class LoadErrorsTests
{
    private const string Unreachable = "Could not reach the server. Check the connection and try again.";

    [TestMethod]
    public async Task LoadResult_ASuccess_AppliesTheValue_AndNamesNothing()
    {
        var loads = new LoadErrors();
        List<int>? applied = null;

        await loads.LoadResultAsync("Events", () => Task.FromResult<(List<int>?, string?)>(([1, 2], null)), v => applied = v);

        CollectionAssert.AreEqual(new[] { 1, 2 }, applied);
        Assert.IsNull(loads.Message);
        Assert.IsFalse(loads.Failed("Events"));
    }

    // A failed read keeps what the list had (apply is not called) and names it; the next success clears it.
    [TestMethod]
    public async Task LoadResult_AFailedRead_KeepsTheListAndNamesIt_UntilASuccessClearsIt()
    {
        var loads = new LoadErrors();
        var applied = new List<string?>();

        await loads.LoadResultAsync("Event metrics", () => Task.FromResult<(string?, string?)>((null, Unreachable)), applied.Add);

        Assert.IsEmpty(applied);
        Assert.AreEqual($"Event metrics could not be loaded. {Unreachable}", loads.Message);
        Assert.IsTrue(loads.Failed("Event metrics"));

        await loads.LoadResultAsync("Event metrics", () => Task.FromResult<(string?, string?)>(("read", null)), applied.Add);

        CollectionAssert.AreEqual(new[] { "read" }, applied);
        Assert.IsNull(loads.Message);
        Assert.IsFalse(loads.Failed("Event metrics"));
    }

    // LoadResultAsync catches nothing itself: a call that throws instead of returning its message throws out of it,
    // applies nothing and records nothing. The pages give it only calls that return their failure (LoadAsync wraps
    // its load in ApiResult.LoadAsync, EventMetricsClient.LoadAllAsync catches its own), so this pins the contract
    // they depend on: a throwing call must not look like a success, and the list it named stays as it was.
    [TestMethod]
    public async Task LoadResult_AReadThatThrows_ThrowsOut_AppliesNothing_AndKeepsTheListsState()
    {
        var loads = new LoadErrors();
        var applied = 0;
        await loads.LoadResultAsync("Games", () => Task.FromResult<(int, string?)>((0, Unreachable)), _ => applied++);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            loads.LoadResultAsync<int>("Games", () => throw new InvalidOperationException("thrown"), _ => applied++));

        Assert.AreEqual(0, applied);
        Assert.IsTrue(loads.Failed("Games"), "the failure recorded before is still named");
        Assert.AreEqual($"Games could not be loaded. {Unreachable}", loads.Message);
    }
}
