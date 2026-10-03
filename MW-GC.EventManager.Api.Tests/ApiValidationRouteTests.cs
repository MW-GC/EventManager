using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// Every body-reading route (#45) through the real Functions classes over in-memory tables:
/// bad bodies are 400, non-JSON is 415, the shared validators run on create AND update, and a
/// rejected request stores nothing.
/// </summary>
[TestClass]
public class ApiValidationRouteTests
{
    private readonly LibraryHarness h = new();

    // One entry per body-reading route. Updates and the comments PATCH target a stored row,
    // so the 400/415 comes from the body and not from a 404.
    public static IEnumerable<object?[]> BodyRoutes =>
    [
        ["CreateGame"], ["UpdateGame"], ["CreateTheme"], ["UpdateTheme"], ["CreateHoliday"], ["UpdateHoliday"],
        ["CreateActivity"], ["UpdateActivity"], ["UpdateActivityComments"],
        ["SaveCustomizedEvent"], ["UpdateEvent"],
    ];

    private Task<IActionResult> Call(string route, HttpRequest req)
    {
        var game = h.SeedGame();
        var theme = new ThemeEntity { Id = Guid.NewGuid(), Name = "Spooky", PartitionKey = "Theme" };
        h.Themes[theme.RowKey] = theme;
        var holiday = new HolidayEntity { Id = Guid.NewGuid(), Name = "Yule", PartitionKey = "Holiday" };
        h.Holidays[holiday.RowKey] = holiday;
        var activityId = Guid.NewGuid();
        h.Activities[activityId.ToString("D")] = new TableEntity("Activity", activityId.ToString("D"))
        {
            ["GameId"] = game.Id, ["Name"] = "Act", ["ThemeIds"] = "[]", ["HolidayIds"] = "[]",
        };
        var eventId = Guid.NewGuid();
        h.Events[eventId.ToString("D")] = new TableEntity("Event", eventId.ToString("D"))
        {
            ["Name"] = "Friday", ["Selections"] = "[]", ["UniqueGamesOnly"] = true,
        };

        return route switch
        {
            "CreateGame" => h.GameApi.Create(req, default),
            "UpdateGame" => h.GameApi.Update(req, game.Id, default),
            "CreateTheme" => h.ThemeApi.Create(req, default),
            "UpdateTheme" => h.ThemeApi.Update(req, theme.Id, default),
            "CreateHoliday" => h.HolidayApi.Create(req, default),
            "UpdateHoliday" => h.HolidayApi.Update(req, holiday.Id, default),
            "CreateActivity" => h.ActivityApi.Create(req, default),
            "UpdateActivity" => h.ActivityApi.Update(req, activityId, default),
            "UpdateActivityComments" => h.ActivityApi.UpdateComments(req, activityId, default),
            "SaveCustomizedEvent" => h.EventApi.SaveCustomized(req, default),
            "UpdateEvent" => h.EventApi.Update(req, eventId, default),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
        };
    }

    [TestMethod]
    [DynamicData(nameof(BodyRoutes))]
    public async Task TruncatedJsonIs400OnEveryRouteAndWritesNothing(string route)
    {
        var result = await Call(route, TestRequests.Raw("{\"name\":"));

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(result));
        Assert.AreEqual(RequestBody.InvalidJsonMessage, ApiResults.Message(result));
        Assert.AreEqual(0, h.Writes());
    }

    [TestMethod]
    [DynamicData(nameof(BodyRoutes))]
    public async Task ValidBodySentAsTextPlainIs415OnEveryRouteAndWritesNothing(string route)
    {
        var result = await Call(route, TestRequests.Raw("{\"name\":\"Valid\",\"count\":1}", "text/plain"));

        Assert.AreEqual(StatusCodes.Status415UnsupportedMediaType, ApiResults.Status(result));
        Assert.AreEqual(RequestBody.UnsupportedMediaTypeMessage, ApiResults.Message(result));
        Assert.AreEqual(0, h.Writes());
    }

    [TestMethod]
    [DynamicData(nameof(BodyRoutes))]
    public async Task JsonNullBodyIs400OnEveryRouteAndWritesNothing(string route)
    {
        var result = await Call(route, TestRequests.Raw("null"));

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(result));
        Assert.AreEqual(RequestBody.NullBodyMessage, ApiResults.Message(result));
        Assert.AreEqual(0, h.Writes());
    }

    // ---- Names on Games, Themes and Holidays: create and update share one rule.

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"name\":\" \"}")]
    [DataRow("{\"name\":\"\"}")]
    [DataRow("{\"name\":null}")]
    public async Task BlankNamesAre400AndNothingIsStored(string body)
    {
        Assert.AreEqual("Name is required.", ApiResults.Message(await h.GameApi.Create(TestRequests.Raw(body), default)));
        Assert.AreEqual("Name is required.", ApiResults.Message(await h.ThemeApi.Create(TestRequests.Raw(body), default)));
        Assert.AreEqual("Name is required.", ApiResults.Message(await h.HolidayApi.Create(TestRequests.Raw(body), default)));

        Assert.IsEmpty(h.Games);
        Assert.IsEmpty(h.Themes);
        Assert.IsEmpty(h.Holidays);
        Assert.AreEqual(0, h.Writes());
    }

    [TestMethod]
    public async Task UpdatesUseTheSameNameRuleAndLeaveTheRowAlone()
    {
        var game = h.SeedGame("Alpha");
        var theme = new ThemeEntity { Id = Guid.NewGuid(), Name = "Spooky", PartitionKey = "Theme" };
        h.Themes[theme.RowKey] = theme;
        var holiday = new HolidayEntity { Id = Guid.NewGuid(), Name = "Yule", PartitionKey = "Holiday" };
        h.Holidays[holiday.RowKey] = holiday;
        var longName = new string('x', 101);

        Assert.AreEqual("Name is required.", ApiResults.Message(await h.GameApi.Update(TestRequests.Raw("{\"name\":\"  \"}"), game.Id, default)));
        Assert.AreEqual("Name must be 100 characters or fewer.", ApiResults.Message(await h.ThemeApi.Update(TestRequests.Json(new { name = longName }), theme.Id, default)));
        Assert.AreEqual("Name is required.", ApiResults.Message(await h.HolidayApi.Update(TestRequests.Raw("{}"), holiday.Id, default)));

        Assert.AreEqual("Alpha", h.Games[game.RowKey].Name);
        Assert.AreEqual("Spooky", h.Themes[theme.RowKey].Name);
        Assert.AreEqual("Yule", h.Holidays[holiday.RowKey].Name);
        Assert.AreEqual(0, h.Writes());
    }

    [TestMethod]
    public async Task OverLongNameIs400NamingTheFieldOnEveryEntity()
    {
        var game = h.SeedGame();
        var name = new string('x', 101);
        const string expected = "Name must be 100 characters or fewer.";

        Assert.AreEqual(expected, ApiResults.Message(await h.GameApi.Create(TestRequests.Json(new { name }), default)));
        Assert.AreEqual(expected, ApiResults.Message(await h.ThemeApi.Create(TestRequests.Json(new { name }), default)));
        Assert.AreEqual(expected, ApiResults.Message(await h.HolidayApi.Create(TestRequests.Json(new { name }), default)));
        Assert.AreEqual(expected, ApiResults.Message(await h.ActivityApi.Create(TestRequests.Json(new { name, gameId = game.Id }), default)));
        Assert.AreEqual(expected, ApiResults.Message(await h.EventApi.SaveCustomized(TestRequests.Json(EventBody(name, game)), default)));

        Assert.ContainsSingle(h.Games);
        Assert.IsEmpty(h.Themes);
        Assert.IsEmpty(h.Holidays);
        Assert.IsEmpty(h.Activities);
        Assert.IsEmpty(h.Events);
        Assert.AreEqual(0, h.Writes());
    }

    [TestMethod]
    public async Task CreateAndUpdateStoreTheTrimmedName()
    {
        var created = ApiResults.Value<GameEntity>(await h.GameApi.Create(TestRequests.Json(new { name = "  Alpha  ", website = "https://a.test" }), default));
        Assert.AreEqual("Alpha", created.Name);
        Assert.AreEqual("Alpha", h.Games[created.RowKey].Name);

        var updated = ApiResults.Value<GameEntity>(await h.GameApi.Update(TestRequests.Json(new { name = "\tBeta " }), created.Id, default));
        Assert.AreEqual("Beta", updated.Name);
        Assert.AreEqual("Beta", h.Games[created.RowKey].Name);

        var theme = ApiResults.Value<ThemeEntity>(await h.ThemeApi.Create(TestRequests.Json(new { name = " Spooky " }), default));
        Assert.AreEqual("Spooky", h.Themes[theme.RowKey].Name);
        var holiday = ApiResults.Value<HolidayEntity>(await h.HolidayApi.Create(TestRequests.Json(new { name = " Yule " }), default));
        Assert.AreEqual("Yule", h.Holidays[holiday.RowKey].Name);
    }

    [TestMethod]
    [DataRow("website", "Website")]
    [DataRow("imageUrl", "ImageUrl")]
    [DataRow("iconUrl", "IconUrl")]
    public async Task OverLongGameUrlIs400NamingTheField(string json, string field)
    {
        var body = new Dictionary<string, string> { ["name"] = "Alpha", [json] = new string('u', 2049) };

        var result = await h.GameApi.Create(TestRequests.Json(body), default);

        Assert.AreEqual($"{field} must be 2048 characters or fewer.", ApiResults.Message(result));
        Assert.IsEmpty(h.Games);
    }

    // ---- Activities: GameId, id lists, text caps, the comments PATCH.

    [TestMethod]
    public async Task ActivityWithNullThemeIdsIsStoredWithEmptyLists()
    {
        var game = h.SeedGame();

        var result = await h.ActivityApi.Create(
            TestRequests.Raw($"{{\"gameId\":\"{game.Id}\",\"name\":\"Act\",\"themeIds\":null,\"holidayIds\":null}}"), default);

        Assert.AreEqual(StatusCodes.Status201Created, ApiResults.Status(result));
        var created = ApiResults.Value<ActivityEntity>(result);
        Assert.IsEmpty(created.ThemeIds);
        Assert.IsEmpty(created.HolidayIds);
        Assert.AreEqual("[]", h.Activities[created.Id.ToString("D")].GetString("ThemeIds"));
        Assert.AreEqual("[]", h.Activities[created.Id.ToString("D")].GetString("HolidayIds"));
        var stored = ApiResults.Value<ActivityEntity>(await h.ActivityApi.Get(TestRequests.Empty(), created.Id, default));
        Assert.IsNotNull(stored.ThemeIds);
        Assert.IsEmpty(stored.ThemeIds);
        Assert.IsNotNull(stored.HolidayIds);
        Assert.IsEmpty(stored.HolidayIds);
    }

    [TestMethod]
    public async Task ActivityIdListsAreDeduplicatedOnCreateAndUpdate()
    {
        var game = h.SeedGame();
        var theme = Guid.NewGuid();
        var body = new { gameId = game.Id, name = "Act", themeIds = new[] { theme, theme }, holidayIds = Array.Empty<Guid>() };

        var created = ApiResults.Value<ActivityEntity>(await h.ActivityApi.Create(TestRequests.Json(body), default));
        Assert.AreSequenceEqual(new[] { theme }, created.ThemeIds);

        var updated = ApiResults.Value<ActivityEntity>(await h.ActivityApi.Update(
            TestRequests.Raw($"{{\"gameId\":\"{game.Id}\",\"name\":\"Act\",\"themeIds\":[\"{theme}\",\"{theme}\"],\"holidayIds\":null}}"), created.Id, default));
        Assert.AreSequenceEqual(new[] { theme }, updated.ThemeIds);
        Assert.IsEmpty(updated.HolidayIds);
    }

    [TestMethod]
    public async Task ActivityWithEmptyOrUnknownGameIdIs400()
    {
        var missing = await h.ActivityApi.Create(TestRequests.Json(new { name = "Act" }), default);
        var empty = await h.ActivityApi.Create(TestRequests.Json(new { name = "Act", gameId = Guid.Empty }), default);
        var unknown = await h.ActivityApi.Create(TestRequests.Json(new { name = "Act", gameId = Guid.NewGuid() }), default);

        Assert.AreEqual("GameId is required.", ApiResults.Message(missing));
        Assert.AreEqual("GameId is required.", ApiResults.Message(empty));
        Assert.IsExactInstanceOfType<BadRequestObjectResult>(unknown);
        Assert.AreEqual(ActivityValidator.GameNotFoundMessage, ApiResults.Message(unknown));
        Assert.IsEmpty(h.Activities);
        Assert.AreEqual(0, h.Writes());
    }

    [TestMethod]
    public async Task ActivityUpdateAlsoChecksTheGameExists()
    {
        var game = h.SeedGame();
        var created = ApiResults.Value<ActivityEntity>(await h.ActivityApi.Create(TestRequests.Json(new { gameId = game.Id, name = "Act" }), default));

        var result = await h.ActivityApi.Update(TestRequests.Json(new { gameId = Guid.NewGuid(), name = "Moved" }), created.Id, default);

        Assert.AreEqual(ActivityValidator.GameNotFoundMessage, ApiResults.Message(result));
        Assert.AreEqual("Act", h.Activities[created.Id.ToString("D")].GetString("Name"));
        Assert.AreEqual(game.Id, h.Activities[created.Id.ToString("D")].GetGuid("GameId"));
    }

    [TestMethod]
    [DataRow("description", "Description")]
    [DataRow("rules", "Rules")]
    [DataRow("setupRequirements", "SetupRequirements")]
    [DataRow("comments", "Comments")]
    public async Task OverLongActivityTextIs400NamingTheField(string json, string field)
    {
        var game = h.SeedGame();
        var body = new Dictionary<string, object> { ["gameId"] = game.Id, ["name"] = "Act", [json] = new string('t', 2001) };

        var result = await h.ActivityApi.Create(TestRequests.Json(body), default);

        Assert.AreEqual($"{field} must be 2000 characters or fewer.", ApiResults.Message(result));
        Assert.IsEmpty(h.Activities);
    }

    [TestMethod]
    public async Task CommentsPatchIsCappedLikeTheActivity()
    {
        var game = h.SeedGame();
        var created = ApiResults.Value<ActivityEntity>(await h.ActivityApi.Create(TestRequests.Json(new { gameId = game.Id, name = "Act" }), default));

        var tooLong = await h.ActivityApi.UpdateComments(TestRequests.Json(new { comments = new string('c', 2001) }), created.Id, default);
        Assert.AreEqual("Comments must be 2000 characters or fewer.", ApiResults.Message(tooLong));
        Assert.IsNull(h.Activities[created.Id.ToString("D")].GetString("Comments"));

        var ok = await h.ActivityApi.UpdateComments(TestRequests.Json(new { comments = "Bring snacks" }), created.Id, default);
        Assert.AreEqual("Bring snacks", ApiResults.Value<ActivityEntity>(ok).Comments);
        Assert.AreEqual("Bring snacks", h.Activities[created.Id.ToString("D")].GetString("Comments"));
    }

    // ---- Events: the name rule on create and update, the rest unchanged.

    [TestMethod]
    [DataRow(" ")]
    [DataRow("")]
    [DataRow(null)]
    public async Task EventWithBlankNameIs400OnCreateAndUpdate(string? name)
    {
        var game = h.SeedGame();
        var saved = ApiResults.Value<EventEntity>(await h.EventApi.SaveCustomized(TestRequests.Json(EventBody("Friday", game)), default));

        var create = await h.EventApi.SaveCustomized(TestRequests.Json(EventBody(name, game)), default);
        var update = await h.EventApi.Update(TestRequests.Json(EventBody(name, game)), saved.Id, default);

        Assert.AreEqual("Name is required.", ApiResults.Message(create));
        Assert.AreEqual("Name is required.", ApiResults.Message(update));
        Assert.AreEqual("Friday", Assert.ContainsSingle(h.Events).Value.GetString("Name"));
    }

    [TestMethod]
    public async Task EventNameIsStoredTrimmed()
    {
        var game = h.SeedGame();

        var saved = ApiResults.Value<EventEntity>(await h.EventApi.SaveCustomized(TestRequests.Json(EventBody("  Friday  ", game)), default));

        Assert.AreEqual("Friday", saved.Name);
        Assert.AreEqual("Friday", h.Events[saved.Id.ToString("D")].GetString("Name"));
    }

    [TestMethod]
    public async Task UnknownIdsStill404BeforeTheBodyIsRead()
    {
        // A missing row is a 404 even when the body is also bad: the lookup comes first, as before.
        Assert.IsExactInstanceOfType<NotFoundResult>(await h.GameApi.Update(TestRequests.Raw("{"), Guid.NewGuid(), default));
        Assert.IsExactInstanceOfType<NotFoundResult>(await h.ActivityApi.UpdateComments(TestRequests.Raw("{", "text/plain"), Guid.NewGuid(), default));
        Assert.IsExactInstanceOfType<NotFoundResult>(await h.EventApi.Update(TestRequests.Raw("null"), Guid.NewGuid(), default));
    }

    private static EventEntity EventBody(string? name, GameEntity game)
    {
        var g = new Game { Id = game.Id, Name = game.Name };
        return new EventEntity
        {
            Name = name!,
            Date = new DateTimeOffset(2026, 10, 2, 18, 0, 0, TimeSpan.Zero),
            Selections = [new Selection { Game = g, Activity = new Activity { Id = Guid.NewGuid(), GameId = g.Id, Name = "Act" } }],
        };
    }
}
