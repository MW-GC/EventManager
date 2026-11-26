using Microsoft.AspNetCore.Http;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Requests;
using static MW_GC.EventManager.Api.Tests.Series.SeriesHarness;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// The three number Filters of a Series' slot template (#204 user story 10, #232): avoid recent, players tonight
/// and longest duration are stored with the Series as each Filter's own setting, read back, written by every
/// Series route that stores a body, and given to the new Series of a "this and following" edit while the old
/// Series keeps its own. Null is off, and a body that leaves them out turns them off.
/// </summary>
[TestClass]
public sealed class SeriesFilterTemplateTests
{
    private readonly SeriesHarness h = new();

    /// <summary>A Series body whose Filters carry the three values; null sends an explicit JSON null.</summary>
    internal static object FilterBody(int? avoidRecent, int? playersTonight, int? longestDuration,
        string name = "Thursday night", string first = First) => new
    {
        name,
        schedule = new { first, rule = new { kind = "Weekly" }, end = (string?)null },
        slotCount = 3,
        uniqueGamesOnly = true,
        filters = new
        {
            themedOnly = true, gameIds = Array.Empty<Guid>(), themeIds = Array.Empty<Guid>(), holidayIds = Array.Empty<Guid>(),
            avoidRecent, playersTonight, longestDuration,
        },
    };

    /// <summary>The three as one line, so a swapped or dropped Filter shows in the failure.</summary>
    internal static string Three(SeriesEntity series) =>
        $"avoidRecent={Show(series.Filters.AvoidRecent)}, playersTonight={Show(series.Filters.PlayersTonight)}, " +
        $"longestDuration={Show(series.Filters.LongestDuration)}";

    private static string Show(int? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "off";

    private const string Off = "avoidRecent=off, playersTonight=off, longestDuration=off";

    private static string Following(Guid occurrence) => $"?scope=following&occurrence={occurrence:D}";

    [TestMethod]
    public async Task A_create_stores_the_three_and_GET_returns_them()
    {
        var (id, change, _) = await h.CreateAsync(FilterBody(avoidRecent: 3, playersTonight: 4, longestDuration: 90));

        Assert.AreEqual("avoidRecent=3, playersTonight=4, longestDuration=90", Three(change.Series!));
        var read = (await h.SeriesAsync(id))!;
        Assert.AreEqual("avoidRecent=3, playersTonight=4, longestDuration=90", Three(read));
        Assert.IsTrue(read.Filters.ThemedOnly);
    }

    [TestMethod]
    [DataRow(false, DisplayName = "left out of the body")]
    [DataRow(true, DisplayName = "sent as null")]
    public async Task A_create_without_them_stores_them_as_off(bool sendNulls)
    {
        var (id, _, _) = await h.CreateAsync(sendNulls ? FilterBody(null, null, null) : Body());

        Assert.AreEqual(Off, Three((await h.SeriesAsync(id))!));
    }

    [TestMethod]
    public async Task A_repeated_create_with_other_values_is_a_conflict_and_with_the_same_values_is_a_replay()
    {
        var key = Guid.NewGuid();
        var (id, _, _) = await h.CreateAsync(FilterBody(3, 4, 90), key);

        var other = await h.SeriesApi.Create(Request(FilterBody(3, 4, 91), key: key), default);
        var same = await h.SeriesApi.Create(Request(FilterBody(3, 4, 90), key: key), default);

        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(other));
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(same));
        Assert.AreEqual("avoidRecent=3, playersTonight=4, longestDuration=90", Three((await h.SeriesAsync(id))!));
    }

    [TestMethod]
    public async Task A_Series_stored_before_the_three_existed_reads_them_as_off_and_a_replace_that_sends_them_stores_them()
    {
        var (id, _, _) = await h.CreateAsync();
        // The Filters value exactly as a Series row stored before #232 holds it.
        h.SeriesRows[id.ToString("D")]["Filters"] = "{\"themedOnly\":false,\"gameIds\":[],\"themeIds\":[],\"holidayIds\":[]}";

        Assert.AreEqual(Off, Three((await h.SeriesAsync(id))!));

        await h.EditAsync(id, FilterBody(5, 6, 120), "?scope=all");

        Assert.AreEqual("avoidRecent=5, playersTonight=6, longestDuration=120", Three((await h.SeriesAsync(id))!));
    }

    [TestMethod]
    public async Task An_all_edit_keeps_the_values_it_sends_again_and_stores_the_ones_it_changes()
    {
        var (id, _, _) = await h.CreateAsync(FilterBody(3, 4, 90));

        var renamed = await h.EditAsync(id, FilterBody(3, 4, 90, name: "Renamed"), "?scope=all");
        Assert.AreEqual("avoidRecent=3, playersTonight=4, longestDuration=90", Three(renamed.Series!));
        Assert.AreEqual("Renamed", (await h.SeriesAsync(id))!.Name);
        Assert.AreEqual("avoidRecent=3, playersTonight=4, longestDuration=90", Three((await h.SeriesAsync(id))!));

        var changed = await h.EditAsync(id, FilterBody(2, 8, 45), "?scope=all");
        Assert.AreEqual("avoidRecent=2, playersTonight=8, longestDuration=45", Three(changed.Series!));
        Assert.AreEqual("avoidRecent=2, playersTonight=8, longestDuration=45", Three((await h.SeriesAsync(id))!));
    }

    [TestMethod]
    public async Task An_edit_from_a_client_that_predates_the_three_turns_them_off()
    {
        // A PUT body is the whole Series: a client that does not know the fields leaves them out, and the API
        // stores what it was sent, as it does for an Activity's players and duration.
        var (id, _, _) = await h.CreateAsync(FilterBody(3, 4, 90));

        await h.EditAsync(id, Body(name: "Changed"), "?scope=all");

        var read = (await h.SeriesAsync(id))!;
        Assert.AreEqual("Changed", read.Name);
        Assert.AreEqual(Off, Three(read));
    }

    [TestMethod]
    public async Task A_following_edit_gives_the_new_Series_its_values_and_the_old_one_keeps_its_own()
    {
        var (id, _, _) = await h.CreateAsync(FilterBody(3, 4, 90));
        var fourth = (await h.OccurrencesAsync(id))[3];

        var change = await h.EditAsync(id,
            FilterBody(2, 8, 45, name: "Late night", first: "2027-03-25T21:00:00-05:00"), Following(fourth.Id));

        Assert.AreEqual("avoidRecent=2, playersTonight=8, longestDuration=45", Three(change.NewSeries!));
        Assert.AreEqual("avoidRecent=3, playersTonight=4, longestDuration=90", Three(change.Series!));
        Assert.AreEqual("avoidRecent=2, playersTonight=8, longestDuration=45", Three((await h.SeriesAsync(change.NewSeries!.Id))!));
        Assert.AreEqual("avoidRecent=3, playersTonight=4, longestDuration=90", Three((await h.SeriesAsync(id))!));
    }
}
