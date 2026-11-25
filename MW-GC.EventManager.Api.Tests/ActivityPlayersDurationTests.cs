using System.Text.Json;
using Microsoft.AspNetCore.Http;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// #98: an Activity's optional players and duration (minimum players, maximum players, duration in minutes)
/// through the real Activity routes and TableStore over in-memory tables. The body of a PUT is the whole
/// Activity, so a field it leaves out is unset.
/// </summary>
[TestClass]
public class ActivityPlayersDurationTests
{
    private readonly LibraryHarness h = new();
    private readonly GameEntity game;

    public ActivityPlayersDurationTests()
    {
        game = h.SeedGame();
        // The old-text conversion already finished (its marker is stored), so a PUT writes only the Activity.
        h.SeedMarker();
    }

    private object Body(string name = "Werewolf", int? minPlayers = null, int? maxPlayers = null, int? durationMinutes = null) =>
        new { gameId = game.Id, name, minPlayers, maxPlayers, durationMinutes };

    private static HttpRequest Request(object body, string? ifMatch = null)
    {
        var request = TestRequests.Json(body);
        if (ifMatch is not null) request.Headers["If-Match"] = ifMatch;
        return request;
    }

    private async Task<ActivityEntity> Create(int? minPlayers, int? maxPlayers, int? durationMinutes)
    {
        var result = await h.ActivityApi.Create(Request(Body(minPlayers: minPlayers, maxPlayers: maxPlayers, durationMinutes: durationMinutes)), default);
        Assert.AreEqual(StatusCodes.Status201Created, ApiResults.Status(result), ApiResultsText(result));
        return ApiResults.Value<ActivityEntity>(result);
    }

    private static string ApiResultsText(Microsoft.AspNetCore.Mvc.IActionResult result) =>
        result is Microsoft.AspNetCore.Mvc.ObjectResult { Value: string text } ? text : result.GetType().Name;

    private async Task<ActivityEntity> Read(Guid id) =>
        ApiResults.Value<ActivityEntity>(await h.ActivityApi.Get(TestRequests.Empty(), id, default));

    private async Task<ActivityEntity> ReadFromList(Guid id) =>
        ApiResults.Value<List<ActivityEntity>>(await h.ActivityApi.GetAll(TestRequests.Empty(), default)).Single(a => a.Id == id);

    private static void AssertValues(ActivityEntity activity, int? minPlayers, int? maxPlayers, int? durationMinutes)
    {
        Assert.AreEqual(minPlayers, activity.MinPlayers, "MinPlayers");
        Assert.AreEqual(maxPlayers, activity.MaxPlayers, "MaxPlayers");
        Assert.AreEqual(durationMinutes, activity.DurationMinutes, "DurationMinutes");
    }

    private void AssertStored(Guid id, int? minPlayers, int? maxPlayers, int? durationMinutes)
    {
        var row = h.Activities[id.ToString("D")];
        Assert.AreEqual(minPlayers, row.GetInt32("MinPlayers"), "stored MinPlayers");
        Assert.AreEqual(maxPlayers, row.GetInt32("MaxPlayers"), "stored MaxPlayers");
        Assert.AreEqual(durationMinutes, row.GetInt32("DurationMinutes"), "stored DurationMinutes");
    }

    // ---- round trip ----------------------------------------------------------------------------

    [TestMethod]
    public async Task CreateStoresTheThreeFields_AndEveryReadReturnsThem()
    {
        var created = await Create(2, 6, 45);

        AssertValues(created, 2, 6, 45);
        AssertStored(created.Id, 2, 6, 45);
        AssertValues(await Read(created.Id), 2, 6, 45);
        AssertValues(await ReadFromList(created.Id), 2, 6, 45);
    }

    [TestMethod]
    public async Task AnUpdateThatSendsThem_KeepsWhatItSends()
    {
        var created = await Create(2, 6, 45);

        var result = await h.ActivityApi.Update(Request(Body(minPlayers: 3, maxPlayers: 8, durationMinutes: 90), created.ETag.ToString()), created.Id, default);

        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(result));
        AssertValues(ApiResults.Value<ActivityEntity>(result), 3, 8, 90);
        AssertStored(created.Id, 3, 8, 90);
        AssertValues(await Read(created.Id), 3, 8, 90);
    }

    // The rule docs/api-validation.md records: the body of a PUT is the whole Activity, so a field it does not
    // send (or sends as null) is unset, as for the other fields. This is how the Activities form clears one.
    [TestMethod]
    public async Task AnUpdateThatOmitsThem_UnsetsThem()
    {
        var created = await Create(2, 6, 45);

        var omitted = await h.ActivityApi.Update(Request(new { gameId = game.Id, name = "Werewolf" }), created.Id, default);

        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(omitted));
        AssertValues(await Read(created.Id), null, null, null);
        AssertStored(created.Id, null, null, null);
    }

    [TestMethod]
    public async Task AnUpdateThatSendsOneAsNull_UnsetsOnlyThatOne()
    {
        var created = await Create(2, 6, 45);

        var result = await h.ActivityApi.Update(Request(Body(minPlayers: 2, maxPlayers: 6, durationMinutes: null)), created.Id, default);

        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(result));
        AssertValues(await Read(created.Id), 2, 6, null);
    }

    [TestMethod]
    public async Task ACreateWithoutThem_StoresNoneSet()
    {
        var result = await h.ActivityApi.Create(Request(new { gameId = game.Id, name = "Plain" }), default);

        Assert.AreEqual(StatusCodes.Status201Created, ApiResults.Status(result));
        AssertValues(await Read(ApiResults.Value<ActivityEntity>(result).Id), null, null, null);
    }

    // ---- old rows ------------------------------------------------------------------------------

    // A row stored before #98 has no such columns at all: it reads back with none set, and saves without error.
    [TestMethod]
    public async Task AnOldRowWithoutTheColumns_ReadsBackWithNoneSet_AndSaves()
    {
        var id = Guid.NewGuid();
        var old = OldActivityRows.Row(id, game.Id, "Old timer", oldColumn: null);
        Assert.IsFalse(old.ContainsKey("MinPlayers") || old.ContainsKey("MaxPlayers") || old.ContainsKey("DurationMinutes"));
        h.Activities[id.ToString("D")] = old;

        AssertValues(await Read(id), null, null, null);
        AssertValues(await ReadFromList(id), null, null, null);

        var saved = await h.ActivityApi.Update(Request(new { gameId = game.Id, name = "Old timer", minPlayers = 4 }), id, default);
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(saved));
        AssertValues(await Read(id), 4, null, null);
    }

    // ---- validation ----------------------------------------------------------------------------

    public static IEnumerable<object?[]> Refused =>
    [
        [0, null, null, "Minimum players must be a whole number from 1 to 99."],
        [-1, null, null, "Minimum players must be a whole number from 1 to 99."],
        [100, null, null, "Minimum players must be a whole number from 1 to 99."],
        [null, 0, null, "Maximum players must be a whole number from 1 to 99."],
        [null, -3, null, "Maximum players must be a whole number from 1 to 99."],
        [null, 100, null, "Maximum players must be a whole number from 1 to 99."],
        [null, null, 0, "Duration in minutes must be a whole number from 1 to 1440."],
        [null, null, -45, "Duration in minutes must be a whole number from 1 to 1440."],
        [null, null, 1441, "Duration in minutes must be a whole number from 1 to 1440."],
        [7, 6, null, "Minimum players cannot be more than maximum players."],
        [99, 1, 45, "Minimum players cannot be more than maximum players."],
        // Two bad fields: the minimum is checked before the duration, so its message wins (as the Web parity grid expects).
        [0, null, 1441, "Minimum players must be a whole number from 1 to 99."],
    ];

    [TestMethod]
    [DynamicData(nameof(Refused))]
    public async Task ACreateOutsideTheRules_Is400InTheActivitysOwnWords_AndWritesNothing(int? min, int? max, int? duration, string message)
    {
        var result = await h.ActivityApi.Create(Request(Body(minPlayers: min, maxPlayers: max, durationMinutes: duration)), default);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(result));
        Assert.AreEqual(message, ApiResults.Message(result));
        Assert.IsEmpty(h.Activities);
        Assert.AreEqual(0, h.Writes());
    }

    [TestMethod]
    [DynamicData(nameof(Refused))]
    public async Task AnUpdateOutsideTheRules_Is400_AndLeavesTheRowAsItWas(int? min, int? max, int? duration, string message)
    {
        var created = await Create(2, 6, 45);
        var writes = h.Writes();

        var result = await h.ActivityApi.Update(Request(Body(minPlayers: min, maxPlayers: max, durationMinutes: duration)), created.Id, default);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(result));
        Assert.AreEqual(message, ApiResults.Message(result));
        Assert.AreEqual(writes, h.Writes());
        AssertStored(created.Id, 2, 6, 45);
    }

    [TestMethod]
    [DataRow(1, 1, 1)]
    [DataRow(1, 99, 1440)]
    [DataRow(99, 99, 1440)]
    [DataRow(4, 4, null)]
    [DataRow(null, 99, null)]
    [DataRow(99, null, null)]
    [DataRow(null, null, 1440)]
    public async Task TheBoundaryValues_AreAccepted(int? min, int? max, int? duration)
    {
        var created = await Create(min, max, duration);

        AssertValues(await Read(created.Id), min, max, duration);
    }

    // A number sent as a JSON string is read as that number (the API reads bodies with the Web JSON defaults, which
    // allow numbers in strings), so it is stored, not refused. The Web client always sends plain numbers.
    [TestMethod]
    public async Task AQuotedWholeNumber_IsReadAsThatNumber()
    {
        var result = await h.ActivityApi.Create(TestRequests.Raw($"{{\"gameId\":\"{game.Id}\",\"name\":\"Act\",\"maxPlayers\":\"6\"}}"), default);

        Assert.AreEqual(StatusCodes.Status201Created, ApiResults.Status(result), ApiResultsText(result));
        var created = ApiResults.Value<ActivityEntity>(result);
        AssertValues(created, null, 6, null);
        AssertStored(created.Id, null, 6, null);
    }

    // A fractional value, or a string that is not a whole number ("six"), cannot be read into the whole-number
    // field: the malformed-body 400.
    [TestMethod]
    [DataRow("2.5")]
    [DataRow("\"six\"")]
    [DataRow("\"2.5\"")]
    public async Task ANonWholeNumber_IsTheMalformedBody400(string raw)
    {
        var result = await h.ActivityApi.Create(TestRequests.Raw($"{{\"gameId\":\"{game.Id}\",\"name\":\"Act\",\"maxPlayers\":{raw}}}"), default);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(result));
        Assert.AreEqual("Request body is empty or malformed.", ApiResults.Message(result));
        Assert.IsEmpty(h.Activities);
    }

    // ---- two admins ----------------------------------------------------------------------------

    // Two admins edit the same Activity: the second, still holding the first ETag, is refused and the first
    // admin's players and duration stay.
    [TestMethod]
    public async Task ASecondEditWithAStaleIfMatch_Is409_AndKeepsTheFirstEditorsValues()
    {
        var created = await Create(2, 6, 45);
        var original = created.ETag.ToString();

        var first = await h.ActivityApi.Update(Request(Body(minPlayers: 3, maxPlayers: 5, durationMinutes: 30), original), created.Id, default);
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(first));

        var second = await h.ActivityApi.Update(Request(Body(minPlayers: 8, maxPlayers: 10, durationMinutes: 120), original), created.Id, default);

        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(second));
        Assert.AreEqual(RouteFailures.ConflictMessage, ApiResults.Message(second));
        AssertStored(created.Id, 3, 5, 30);
    }

    // ---- duplicate -----------------------------------------------------------------------------

    [TestMethod]
    public async Task ADuplicate_CarriesThePlayersAndDuration()
    {
        var created = await Create(2, 6, 45);

        var result = await h.ActivityApi.Duplicate(TestRequests.Empty(), created.Id, default);

        Assert.AreEqual(StatusCodes.Status201Created, ApiResults.Status(result));
        var copy = ApiResults.Value<ActivityEntity>(result);
        Assert.AreNotEqual(created.Id, copy.Id);
        AssertValues(copy, 2, 6, 45);
        AssertStored(copy.Id, 2, 6, 45);
    }
}

/// <summary>
/// #98: a Selection snapshot keeps the players and duration the Activity had when it was chosen, and an Event
/// stored before #98 (its snapshot has no such values) reads back with none set.
/// </summary>
[TestClass]
public class SelectionSnapshotPlayersDurationTests
{
    private readonly LibraryHarness h = new();
    private static readonly Guid GameId = Guid.Parse("0f4c7a2e-6b1d-4e39-8a57-2c9d1b0e3f64");
    private static readonly Guid ActivityId = Guid.Parse("5d8e2b17-3c4a-4f60-9b21-7e0a6c3d1f95");

    [TestMethod]
    public async Task AnEventSavedWithASnapshotCarryingTheValues_ReadsBackWithThem()
    {
        var game = h.SeedGame("Kart");
        var body = new EventEntity
        {
            Name = "Game night",
            Date = new DateTimeOffset(2026, 12, 4, 19, 0, 0, TimeSpan.Zero),
            Selections = [new Selection
            {
                Game = new Game { Id = game.Id, Name = "Kart" },
                Activity = new Activity { Id = ActivityId, GameId = game.Id, Name = "Grand Prix", MinPlayers = 2, MaxPlayers = 6, DurationMinutes = 45 }
            }]
        };

        var created = await h.EventApi.SaveCustomized(TestRequests.Json(body), default);

        Assert.AreEqual(StatusCodes.Status201Created, ApiResults.Status(created));
        var id = ApiResults.Value<EventEntity>(created).Id;
        var activity = Assert.ContainsSingle(ApiResults.Value<EventEntity>(await h.EventApi.Get(TestRequests.Empty(), id, default)).Selections).Activity;
        Assert.AreEqual(2, activity.MinPlayers);
        Assert.AreEqual(6, activity.MaxPlayers);
        Assert.AreEqual(45, activity.DurationMinutes);
        StringAssert.Contains(h.Events[id.ToString("D")].GetString("Selections"), "\"minPlayers\":2");
    }

    [TestMethod]
    public async Task AnEventStoredBeforeTheValues_ReadsBackWithNoneSet()
    {
        var id = Guid.NewGuid();
        // The Selections column as an Event stored before #98 has it: no players or duration in the snapshot.
        var selections = "[{\"game\":{\"id\":\"" + GameId + "\",\"name\":\"Kart\"},\"activity\":{\"id\":\"" + ActivityId +
                         "\",\"gameId\":\"" + GameId + "\",\"name\":\"Grand Prix\",\"description\":\"Four races\"}}]";
        h.Events[id.ToString("D")] = new Azure.Data.Tables.TableEntity("Event", id.ToString("D"))
        {
            ["Name"] = "Old night", ["Date"] = new DateTimeOffset(2025, 10, 9, 19, 0, 0, TimeSpan.Zero),
            ["Selections"] = selections, ["UniqueGamesOnly"] = true,
        };

        var read = ApiResults.Value<EventEntity>(await h.EventApi.Get(TestRequests.Empty(), id, default));

        var activity = Assert.ContainsSingle(read.Selections).Activity;
        Assert.AreEqual("Four races", activity.Description);
        Assert.IsNull(activity.MinPlayers);
        Assert.IsNull(activity.MaxPlayers);
        Assert.IsNull(activity.DurationMinutes);
    }
}
