using MW_GC.EventManager.API.Functions;
using MW_GC.EventManager.API.Services;
using static MW_GC.EventManager.Api.Tests.Series.SeriesTestSupport;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// The ids derived for stored rows (#204). Stored Occurrences and "this and following" Series are found again
/// by these ids, so they must never change after release. The expected values below were computed outside
/// .NET (Python: <c>uuid.UUID(bytes_le=sha256(text.encode()).digest()[:16])</c>, where <c>text</c> is the
/// documented string and the ticks are .NET ticks of the UTC instant), never with the code under test.
/// </summary>
[TestClass]
public sealed class OccurrenceIdTests
{
    private static readonly Guid Series = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [TestMethod]
    public void An_Occurrence_id_is_pinned_to_a_literal()
    {
        // text: series-occurrence:11111111-2222-3333-4444-555555555555:639398016000000000
        Assert.AreEqual(Guid.Parse("977e2ff9-168d-89b8-c28c-b2c036ff7549"),
            SeriesOccurrences.OccurrenceId(Series, At("2027-03-04T19:00:00-05:00")));
        // text: series-occurrence:11111111-2222-3333-4444-555555555555:639404064000000000
        Assert.AreEqual(Guid.Parse("e133287e-6af8-4ede-a992-448dc163758b"),
            SeriesOccurrences.OccurrenceId(Series, At("2027-03-11T19:00:00-05:00")));
    }

    [TestMethod]
    public void The_same_instant_at_another_offset_is_the_same_Occurrence() =>
        Assert.AreEqual(
            SeriesOccurrences.OccurrenceId(Series, At("2027-03-04T19:00:00-05:00")),
            SeriesOccurrences.OccurrenceId(Series, At("2027-03-05T00:00:00+00:00")));

    [TestMethod]
    public void Another_Series_at_the_same_instant_is_another_Occurrence() =>
        Assert.AreNotEqual(
            SeriesOccurrences.OccurrenceId(Series, At("2027-03-04T19:00:00-05:00")),
            SeriesOccurrences.OccurrenceId(Guid.Parse("11111111-2222-3333-4444-555555555556"), At("2027-03-04T19:00:00-05:00")));

    [TestMethod]
    public void A_following_Series_id_is_pinned_to_a_literal() =>
        // text: series-following:11111111-2222-3333-4444-555555555555:639410112000000000
        Assert.AreEqual(Guid.Parse("a6ce9687-bbdb-1ac5-23c2-ccf27958544a"),
            SeriesFunctions.FollowingSeriesId(Series, At("2027-03-18T19:00:00-05:00")));
}
