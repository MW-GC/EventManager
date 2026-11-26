using Bunit;

namespace MW_GC.EventManager.Web.Tests;

// #204, #232: a series scope sends the Series back as read, so an edit through the dialog never clears the stored
// avoid recent, players tonight and longest duration. The Raid Series holds all three, distinct, beside its other
// Filters. Fixture: OccurrenceEditFixture.
[TestClass]
public sealed class OccurrenceEditSeriesFiltersTests : OccurrenceEditFixture
{
    [TestMethod]
    [DataRow("All future events")]
    [DataRow("This and following events")]
    public async Task ASeriesScope_SendsTheStoredAvoidRecentPlayersAndDuration_Back(string option)
    {
        raidSeries.Filters.AvoidRecent = 4;
        raidSeries.Filters.PlayersTonight = 6;
        raidSeries.Filters.LongestDuration = 90;
        var page = await OpenEdit("Weekly raid");
        await Rename(page, "Raid night");
        await Update(page);

        await ChooseAndConfirm(page, option);

        page.WaitForAssertion(() => Assert.IsFalse(page.HasFormDialog()));
        var sent = SentSeries(RaidSeries).Filters;
        Assert.AreEqual(((int?)4, (int?)6, (int?)90), (sent.AvoidRecent, sent.PlayersTonight, sent.LongestDuration));
        Assert.IsTrue(sent.ThemedOnly);
        CollectionAssert.AreEqual(raidSeries.Filters.GameIds, sent.GameIds);
    }
}
