using MW_GC.EventManager.Web.Shared.Activities;

namespace MW_GC.EventManager.Web.Tests;

// #98: the one wording of an Activity's players and duration, shared by the Event details dialog (and later the
// announcement text). Every expected string is written out by hand.
[TestClass]
public sealed class PlayersDurationTextTests
{
    [TestMethod]
    [DataRow(2, 6, "2-6 players")]
    [DataRow(1, 99, "1-99 players")]
    [DataRow(null, 6, "up to 6 players")]
    [DataRow(null, 1, "up to 1 player")]
    [DataRow(2, null, "2 or more players")]
    [DataRow(1, null, "1 or more players")]
    [DataRow(4, 4, "4 players")]
    [DataRow(1, 1, "1 player")]
    public void Players_ReadsTheBoundsThatAreSet(int? min, int? max, string expected) =>
        Assert.AreEqual(expected, PlayersDurationText.Players(min, max));

    [TestMethod]
    public void Players_WithNeitherBound_IsNothing() => Assert.IsNull(PlayersDurationText.Players(null, null));

    [TestMethod]
    [DataRow(45, "45 min")]
    [DataRow(1, "1 min")]
    [DataRow(1440, "1440 min")]
    public void Duration_IsMinutes(int minutes, string expected) =>
        Assert.AreEqual(expected, PlayersDurationText.Duration(minutes));

    [TestMethod]
    public void Duration_NotSet_IsNothing() => Assert.IsNull(PlayersDurationText.Duration(null));
}
