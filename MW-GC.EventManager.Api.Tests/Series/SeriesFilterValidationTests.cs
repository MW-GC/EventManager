using Microsoft.AspNetCore.Http;
using MW_GC.EventManager.Shared.Requests;
using static MW_GC.EventManager.Api.Tests.Series.SeriesFilterTemplateTests;
using static MW_GC.EventManager.Api.Tests.Series.SeriesHarness;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// The range rules of a Series' avoid recent, players tonight and longest duration Filters (#232): each is off
/// (null) or a whole number from 1 to its limit, the limits of the Events page's Filters: 10 recent Events, the
/// Activity players limit (99) and the Activity duration limit (1440 minutes). A value outside answers 400 with
/// one message per field, naming it as the Filters panel does, on create and on both edit scopes, and writes
/// nothing.
/// </summary>
[TestClass]
public sealed class SeriesFilterValidationTests
{
    private readonly SeriesHarness h = new();

    // The messages and limits are written out, not read from the code, so a changed limit or wording fails here.
    private const string AvoidRecentMessage = "Avoid recent must be a whole number from 1 to 10.";
    private const string PlayersTonightMessage = "Players tonight must be a whole number from 1 to 99.";
    private const string LongestDurationMessage = "Longest duration must be a whole number from 1 to 1440.";

    public static IEnumerable<object?[]> Refused =>
    [
        [0, null, null, AvoidRecentMessage],
        [11, null, null, AvoidRecentMessage],
        [-1, null, null, AvoidRecentMessage],
        [null, 0, null, PlayersTonightMessage],
        [null, 100, null, PlayersTonightMessage],
        [null, -1, null, PlayersTonightMessage],
        [null, null, 0, LongestDurationMessage],
        [null, null, 1441, LongestDurationMessage],
        [null, null, -1, LongestDurationMessage],
    ];

    public static IEnumerable<object?[]> Accepted =>
    [
        [1, null, null],
        [10, null, null],
        [null, 1, null],
        [null, 99, null],
        [null, null, 1],
        [null, null, 1440],
        [1, 1, 1],
        [10, 99, 1440],
    ];

    [TestMethod]
    [DynamicData(nameof(Refused))]
    public async Task A_create_with_a_value_outside_its_range_is_refused_and_writes_nothing(int? avoid, int? players, int? duration, string message)
    {
        var result = await h.SeriesApi.Create(Request(FilterBody(avoid, players, duration), key: Guid.NewGuid()), default);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(result));
        Assert.AreEqual(message, ApiResults.Message(result));
        Assert.IsEmpty(h.SeriesRows);
        Assert.IsEmpty(h.Events);
    }

    [TestMethod]
    [DynamicData(nameof(Refused))]
    public async Task An_all_edit_with_a_value_outside_its_range_is_refused_and_keeps_the_stored_values(int? avoid, int? players, int? duration, string message)
    {
        var (id, _, _) = await h.CreateAsync(FilterBody(3, 4, 90));

        var result = await h.SeriesApi.Update(Request(FilterBody(avoid, players, duration, name: "Changed"), query: "?scope=all"), id, default);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(result));
        Assert.AreEqual(message, ApiResults.Message(result));
        var read = (await h.SeriesAsync(id))!;
        Assert.AreEqual("Thursday night", read.Name);
        Assert.AreEqual("avoidRecent=3, playersTonight=4, longestDuration=90", Three(read));
    }

    [TestMethod]
    [DynamicData(nameof(Refused))]
    public async Task A_following_edit_with_a_value_outside_its_range_is_refused_and_makes_no_new_Series(int? avoid, int? players, int? duration, string message)
    {
        var (id, _, _) = await h.CreateAsync(FilterBody(3, 4, 90));
        var fourth = (await h.OccurrencesAsync(id))[3];

        var result = await h.SeriesApi.Update(
            Request(FilterBody(avoid, players, duration, first: "2027-03-25T21:00:00-05:00"), query: $"?scope=following&occurrence={fourth.Id:D}"),
            id, default);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(result));
        Assert.AreEqual(message, ApiResults.Message(result));
        Assert.HasCount(1, h.SeriesRows);
        Assert.IsNull((await h.SeriesAsync(id))!.Schedule.End);
    }

    [TestMethod]
    [DynamicData(nameof(Accepted))]
    public async Task A_create_with_each_value_at_a_limit_is_stored(int? avoid, int? players, int? duration)
    {
        var result = await h.SeriesApi.Create(Request(FilterBody(avoid, players, duration), key: Guid.NewGuid()), default);

        Assert.AreEqual(StatusCodes.Status201Created, ApiResults.Status(result), ApiResults.Status(result) == 400 ? ApiResults.Message(result) : null);
        var read = (await h.SeriesAsync(ApiResults.Value<SeriesChange>(result).Series!.Id))!;
        Assert.AreEqual(avoid, read.Filters.AvoidRecent);
        Assert.AreEqual(players, read.Filters.PlayersTonight);
        Assert.AreEqual(duration, read.Filters.LongestDuration);
    }

    [TestMethod]
    [DynamicData(nameof(Accepted))]
    public async Task An_all_edit_with_each_value_at_a_limit_is_stored(int? avoid, int? players, int? duration)
    {
        var (id, _, _) = await h.CreateAsync();

        await h.EditAsync(id, FilterBody(avoid, players, duration), "?scope=all");

        var read = (await h.SeriesAsync(id))!;
        Assert.AreEqual(avoid, read.Filters.AvoidRecent);
        Assert.AreEqual(players, read.Filters.PlayersTonight);
        Assert.AreEqual(duration, read.Filters.LongestDuration);
    }

    [TestMethod]
    public async Task The_first_field_out_of_range_names_itself_when_several_are()
    {
        var result = await h.SeriesApi.Create(Request(FilterBody(0, 0, 0), key: Guid.NewGuid()), default);
        var later = await h.SeriesApi.Create(Request(FilterBody(3, 100, 0), key: Guid.NewGuid()), default);

        Assert.AreEqual(AvoidRecentMessage, ApiResults.Message(result));
        Assert.AreEqual(PlayersTonightMessage, ApiResults.Message(later));
    }

    [TestMethod]
    public async Task A_fractional_value_is_the_malformed_body()
    {
        const string json = """
            {"name":"Thursday night","schedule":{"first":"2027-03-04T19:00:00-05:00","rule":{"kind":"Weekly"}},
             "slotCount":3,"uniqueGamesOnly":true,"filters":{"playersTonight":2.5}}
            """;

        var result = await h.SeriesApi.Create(Raw(json, Guid.NewGuid()), default);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(result));
        Assert.AreEqual("Request body is empty or malformed.", ApiResults.Message(result));
        Assert.IsEmpty(h.SeriesRows);
    }
}
