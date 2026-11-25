using MW_GC.EventManager.Shared.Entities;
using static MW_GC.EventManager.Api.Tests.Series.SeriesHarness;
using static MW_GC.EventManager.Api.Tests.Series.SeriesTestSupport;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// The split marks are owned by the API (#204): a <c>splitFrom</c> or <c>splitAt</c> a request body sends is
/// ignored on a create, an <c>all</c> edit and a "this and following" edit. A Series no split touched stores
/// neither; a split stores the key of the chosen Occurrence on both halves.
/// </summary>
[TestClass]
public sealed class SeriesSplitMarksBodyTests
{
    private const string All = "?scope=all";

    // Marks no split would ever write: a key in 2030, far from every Occurrence of these Series.
    private const string ForeignMark = "2030-01-01T00:00:00+00:00";

    private readonly SeriesHarness h = new();

    // A Series body that also sends both split marks.
    private static object BodyWithMarks(string first = First, string? splitFrom = ForeignMark, string? splitAt = ForeignMark) => new
    {
        name = "Marked",
        schedule = new { first, rule = new { kind = "Weekly" }, end = (string?)null },
        slotCount = 3,
        uniqueGamesOnly = true,
        splitFrom,
        splitAt,
        filters = new { themedOnly = false, gameIds = Array.Empty<Guid>(), themeIds = Array.Empty<Guid>(), holidayIds = Array.Empty<Guid>() },
    };

    [TestMethod]
    public async Task A_create_ignores_split_marks_the_body_sends()
    {
        var (id, change, _) = await h.CreateAsync(BodyWithMarks());

        var stored = (await h.SeriesAsync(id))!;
        Assert.IsNull(stored.SplitFrom);
        Assert.IsNull(stored.SplitAt);
        Assert.IsNull(change.Series!.SplitFrom);
        Assert.IsNull(change.Series.SplitAt);
    }

    [TestMethod]
    public async Task A_create_with_split_marks_is_not_held_by_them()
    {
        // Had the create stored the body's splitFrom, an edit could not move the start earlier than the stored
        // one. A Series no split started has no such limit.
        var (id, _, _) = await h.CreateAsync(BodyWithMarks());

        var change = await h.EditAsync(id, Body(name: "Earlier", first: "2027-03-04T18:00:00-05:00"), All);

        Assert.AreEqual("Earlier", change.Series!.Name);
        Assert.AreEqual("2027-03-04T18:00:00-05:00", Dates(await h.OccurrencesAsync(id))[0]);
    }

    [TestMethod]
    public async Task An_all_edit_ignores_split_marks_the_body_sends()
    {
        var (id, _, _) = await h.CreateAsync();

        await h.EditAsync(id, BodyWithMarks(first: "2027-03-04T20:00:00-05:00"), All);

        var stored = (await h.SeriesAsync(id))!;
        Assert.IsNull(stored.SplitFrom);
        Assert.IsNull(stored.SplitAt);
    }

    [TestMethod]
    public async Task A_following_edit_stores_the_chosen_key_not_the_split_marks_the_body_sends()
    {
        var (id, _, _) = await h.CreateAsync();
        var fourth = (await h.OccurrencesAsync(id))[3];

        var change = await h.EditAsync(id, BodyWithMarks(first: "2027-03-25T21:00:00-05:00"), $"?scope=following&occurrence={fourth.Id:D}");

        SeriesEntity old = (await h.SeriesAsync(id))!, next = (await h.SeriesAsync(change.NewSeries!.Id))!;
        Assert.AreEqual(At("2027-03-25T19:00:00-05:00"), old.SplitAt);
        Assert.IsNull(old.SplitFrom);
        Assert.AreEqual(At("2027-03-25T19:00:00-05:00"), next.SplitFrom);
        Assert.IsNull(next.SplitAt);
    }
}
