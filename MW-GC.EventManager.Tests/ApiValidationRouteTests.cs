using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using Xunit;

namespace MW_GC.EventManager.Tests;

/// <summary>
/// Every body-reading route (#45) through the real Functions classes over in-memory tables:
/// bad bodies are 400, non-JSON is 415, the shared validators run on create AND update, and a
/// rejected request stores nothing.
/// </summary>
public class ApiValidationRouteTests
{
    private readonly LibraryHarness h = new();

    // One entry per body-reading route. Updates and the comments PATCH target a stored row,
    // so the 400/415 comes from the body and not from a 404.
    public static TheoryData<string> BodyRoutes => new()
    {
        "CreateGame", "UpdateGame", "CreateTheme", "UpdateTheme", "CreateHoliday", "UpdateHoliday",
        "CreateActivity", "UpdateActivity", "UpdateActivityComments",
        "GenerateEvent", "SaveCustomizedEvent", "UpdateEvent",
    };

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
            "GenerateEvent" => h.EventApi.Generate(req, default),
            "SaveCustomizedEvent" => h.EventApi.SaveCustomized(req, default),
            "UpdateEvent" => h.EventApi.Update(req, eventId, default),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
        };
    }

    [Theory]
    [MemberData(nameof(BodyRoutes))]
    public async Task TruncatedJsonIs400OnEveryRouteAndWritesNothing(string route)
    {
        var result = await Call(route, TestRequests.Raw("{\"name\":"));

        Assert.Equal(StatusCodes.Status400BadRequest, ApiResults.Status(result));
        Assert.Equal(RequestBody.InvalidJsonMessage, ApiResults.Message(result));
        Assert.Equal(0, h.Writes());
    }

    [Theory]
    [MemberData(nameof(BodyRoutes))]
    public async Task ValidBodySentAsTextPlainIs415OnEveryRouteAndWritesNothing(string route)
    {
        var result = await Call(route, TestRequests.Raw("{\"name\":\"Valid\",\"count\":1}", "text/plain"));

        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, ApiResults.Status(result));
        Assert.Equal(RequestBody.UnsupportedMediaTypeMessage, ApiResults.Message(result));
        Assert.Equal(0, h.Writes());
    }

    [Theory]
    [MemberData(nameof(BodyRoutes))]
    public async Task JsonNullBodyIs400OnEveryRouteAndWritesNothing(string route)
    {
        var result = await Call(route, TestRequests.Raw("null"));

        Assert.Equal(StatusCodes.Status400BadRequest, ApiResults.Status(result));
        Assert.Equal(RequestBody.NullBodyMessage, ApiResults.Message(result));
        Assert.Equal(0, h.Writes());
    }

    // ---- Names on Games, Themes and Holidays: create and update share one rule.

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":\" \"}")]
    [InlineData("{\"name\":\"\"}")]
    [InlineData("{\"name\":null}")]
    public async Task BlankNamesAre400AndNothingIsStored(string body)
    {
        Assert.Equal("Name is required.", ApiResults.Message(await h.GameApi.Create(TestRequests.Raw(body), default)));
        Assert.Equal("Name is required.", ApiResults.Message(await h.ThemeApi.Create(TestRequests.Raw(body), default)));
        Assert.Equal("Name is required.", ApiResults.Message(await h.HolidayApi.Create(TestRequests.Raw(body), default)));

        Assert.Empty(h.Games);
        Assert.Empty(h.Themes);
        Assert.Empty(h.Holidays);
        Assert.Equal(0, h.Writes());
    }

    [Fact]
    public async Task UpdatesUseTheSameNameRuleAndLeaveTheRowAlone()
    {
        var game = h.SeedGame("Alpha");
        var theme = new ThemeEntity { Id = Guid.NewGuid(), Name = "Spooky", PartitionKey = "Theme" };
        h.Themes[theme.RowKey] = theme;
        var holiday = new HolidayEntity { Id = Guid.NewGuid(), Name = "Yule", PartitionKey = "Holiday" };
        h.Holidays[holiday.RowKey] = holiday;
        var longName = new string('x', 101);

        Assert.Equal("Name is required.", ApiResults.Message(await h.GameApi.Update(TestRequests.Raw("{\"name\":\"  \"}"), game.Id, default)));
        Assert.Equal("Name must be 100 characters or fewer.", ApiResults.Message(await h.ThemeApi.Update(TestRequests.Json(new { name = longName }), theme.Id, default)));
        Assert.Equal("Name is required.", ApiResults.Message(await h.HolidayApi.Update(TestRequests.Raw("{}"), holiday.Id, default)));

        Assert.Equal("Alpha", h.Games[game.RowKey].Name);
        Assert.Equal("Spooky", h.Themes[theme.RowKey].Name);
        Assert.Equal("Yule", h.Holidays[holiday.RowKey].Name);
        Assert.Equal(0, h.Writes());
    }

    [Fact]
    public async Task OverLongNameIs400NamingTheFieldOnEveryEntity()
    {
        var game = h.SeedGame();
        var name = new string('x', 101);
        const string expected = "Name must be 100 characters or fewer.";

        Assert.Equal(expected, ApiResults.Message(await h.GameApi.Create(TestRequests.Json(new { name }), default)));
        Assert.Equal(expected, ApiResults.Message(await h.ThemeApi.Create(TestRequests.Json(new { name }), default)));
        Assert.Equal(expected, ApiResults.Message(await h.HolidayApi.Create(TestRequests.Json(new { name }), default)));
        Assert.Equal(expected, ApiResults.Message(await h.ActivityApi.Create(TestRequests.Json(new { name, gameId = game.Id }), default)));
        Assert.Equal(expected, ApiResults.Message(await h.EventApi.SaveCustomized(TestRequests.Json(EventBody(name, game)), default)));

        Assert.Single(h.Games);
        Assert.Empty(h.Themes);
        Assert.Empty(h.Holidays);
        Assert.Empty(h.Activities);
        Assert.Empty(h.Events);
        Assert.Equal(0, h.Writes());
    }

    [Fact]
    public async Task CreateAndUpdateStoreTheTrimmedName()
    {
        var created = ApiResults.Value<GameEntity>(await h.GameApi.Create(TestRequests.Json(new { name = "  Alpha  ", website = "https://a.test" }), default));
        Assert.Equal("Alpha", created.Name);
        Assert.Equal("Alpha", h.Games[created.RowKey].Name);

        var updated = ApiResults.Value<GameEntity>(await h.GameApi.Update(TestRequests.Json(new { name = "\tBeta " }), created.Id, default));
        Assert.Equal("Beta", updated.Name);
        Assert.Equal("Beta", h.Games[created.RowKey].Name);

        var theme = ApiResults.Value<ThemeEntity>(await h.ThemeApi.Create(TestRequests.Json(new { name = " Spooky " }), default));
        Assert.Equal("Spooky", h.Themes[theme.RowKey].Name);
        var holiday = ApiResults.Value<HolidayEntity>(await h.HolidayApi.Create(TestRequests.Json(new { name = " Yule " }), default));
        Assert.Equal("Yule", h.Holidays[holiday.RowKey].Name);
    }

    [Theory]
    [InlineData("website", "Website")]
    [InlineData("imageUrl", "ImageUrl")]
    [InlineData("iconUrl", "IconUrl")]
    public async Task OverLongGameUrlIs400NamingTheField(string json, string field)
    {
        var body = new Dictionary<string, string> { ["name"] = "Alpha", [json] = new string('u', 2049) };

        var result = await h.GameApi.Create(TestRequests.Json(body), default);

        Assert.Equal($"{field} must be 2048 characters or fewer.", ApiResults.Message(result));
        Assert.Empty(h.Games);
    }

    // ---- Activities: GameId, id lists, text caps, the comments PATCH.

    [Fact]
    public async Task ActivityWithNullThemeIdsIsStoredWithEmptyLists()
    {
        var game = h.SeedGame();

        var result = await h.ActivityApi.Create(
            TestRequests.Raw($"{{\"gameId\":\"{game.Id}\",\"name\":\"Act\",\"themeIds\":null,\"holidayIds\":null}}"), default);

        Assert.Equal(StatusCodes.Status201Created, ApiResults.Status(result));
        var created = ApiResults.Value<ActivityEntity>(result);
        Assert.Empty(created.ThemeIds);
        Assert.Empty(created.HolidayIds);
        Assert.Equal("[]", h.Activities[created.Id.ToString("D")].GetString("ThemeIds"));
        Assert.Equal("[]", h.Activities[created.Id.ToString("D")].GetString("HolidayIds"));
        var stored = ApiResults.Value<ActivityEntity>(await h.ActivityApi.Get(TestRequests.Empty(), created.Id, default));
        Assert.NotNull(stored.ThemeIds);
        Assert.Empty(stored.ThemeIds);
        Assert.NotNull(stored.HolidayIds);
        Assert.Empty(stored.HolidayIds);
    }

    [Fact]
    public async Task ActivityIdListsAreDeduplicatedOnCreateAndUpdate()
    {
        var game = h.SeedGame();
        var theme = Guid.NewGuid();
        var body = new { gameId = game.Id, name = "Act", themeIds = new[] { theme, theme }, holidayIds = Array.Empty<Guid>() };

        var created = ApiResults.Value<ActivityEntity>(await h.ActivityApi.Create(TestRequests.Json(body), default));
        Assert.Equal(new[] { theme }, created.ThemeIds);

        var updated = ApiResults.Value<ActivityEntity>(await h.ActivityApi.Update(
            TestRequests.Raw($"{{\"gameId\":\"{game.Id}\",\"name\":\"Act\",\"themeIds\":[\"{theme}\",\"{theme}\"],\"holidayIds\":null}}"), created.Id, default));
        Assert.Equal(new[] { theme }, updated.ThemeIds);
        Assert.Empty(updated.HolidayIds);
    }

    [Fact]
    public async Task ActivityWithEmptyOrUnknownGameIdIs400()
    {
        var missing = await h.ActivityApi.Create(TestRequests.Json(new { name = "Act" }), default);
        var empty = await h.ActivityApi.Create(TestRequests.Json(new { name = "Act", gameId = Guid.Empty }), default);
        var unknown = await h.ActivityApi.Create(TestRequests.Json(new { name = "Act", gameId = Guid.NewGuid() }), default);

        Assert.Equal("GameId is required.", ApiResults.Message(missing));
        Assert.Equal("GameId is required.", ApiResults.Message(empty));
        Assert.IsType<BadRequestObjectResult>(unknown);
        Assert.Equal(ActivityValidator.GameNotFoundMessage, ApiResults.Message(unknown));
        Assert.Empty(h.Activities);
        Assert.Equal(0, h.Writes());
    }

    [Fact]
    public async Task ActivityUpdateAlsoChecksTheGameExists()
    {
        var game = h.SeedGame();
        var created = ApiResults.Value<ActivityEntity>(await h.ActivityApi.Create(TestRequests.Json(new { gameId = game.Id, name = "Act" }), default));

        var result = await h.ActivityApi.Update(TestRequests.Json(new { gameId = Guid.NewGuid(), name = "Moved" }), created.Id, default);

        Assert.Equal(ActivityValidator.GameNotFoundMessage, ApiResults.Message(result));
        Assert.Equal("Act", h.Activities[created.Id.ToString("D")].GetString("Name"));
        Assert.Equal(game.Id, h.Activities[created.Id.ToString("D")].GetGuid("GameId"));
    }

    [Theory]
    [InlineData("description", "Description")]
    [InlineData("rules", "Rules")]
    [InlineData("setupRequirements", "SetupRequirements")]
    [InlineData("comments", "Comments")]
    public async Task OverLongActivityTextIs400NamingTheField(string json, string field)
    {
        var game = h.SeedGame();
        var body = new Dictionary<string, object> { ["gameId"] = game.Id, ["name"] = "Act", [json] = new string('t', 2001) };

        var result = await h.ActivityApi.Create(TestRequests.Json(body), default);

        Assert.Equal($"{field} must be 2000 characters or fewer.", ApiResults.Message(result));
        Assert.Empty(h.Activities);
    }

    [Fact]
    public async Task CommentsPatchIsCappedLikeTheActivity()
    {
        var game = h.SeedGame();
        var created = ApiResults.Value<ActivityEntity>(await h.ActivityApi.Create(TestRequests.Json(new { gameId = game.Id, name = "Act" }), default));

        var tooLong = await h.ActivityApi.UpdateComments(TestRequests.Json(new { comments = new string('c', 2001) }), created.Id, default);
        Assert.Equal("Comments must be 2000 characters or fewer.", ApiResults.Message(tooLong));
        Assert.Null(h.Activities[created.Id.ToString("D")].GetString("Comments"));

        var ok = await h.ActivityApi.UpdateComments(TestRequests.Json(new { comments = "Bring snacks" }), created.Id, default);
        Assert.Equal("Bring snacks", ApiResults.Value<ActivityEntity>(ok).Comments);
        Assert.Equal("Bring snacks", h.Activities[created.Id.ToString("D")].GetString("Comments"));
    }

    // ---- Events: the name rule on create and update, the rest unchanged.

    [Theory]
    [InlineData(" ")]
    [InlineData("")]
    [InlineData(null)]
    public async Task EventWithBlankNameIs400OnCreateAndUpdate(string? name)
    {
        var game = h.SeedGame();
        var saved = ApiResults.Value<EventEntity>(await h.EventApi.SaveCustomized(TestRequests.Json(EventBody("Friday", game)), default));

        var create = await h.EventApi.SaveCustomized(TestRequests.Json(EventBody(name, game)), default);
        var update = await h.EventApi.Update(TestRequests.Json(EventBody(name, game)), saved.Id, default);

        Assert.Equal("Name is required.", ApiResults.Message(create));
        Assert.Equal("Name is required.", ApiResults.Message(update));
        Assert.Equal("Friday", Assert.Single(h.Events).Value.GetString("Name"));
    }

    [Fact]
    public async Task EventNameIsStoredTrimmed()
    {
        var game = h.SeedGame();

        var saved = ApiResults.Value<EventEntity>(await h.EventApi.SaveCustomized(TestRequests.Json(EventBody("  Friday  ", game)), default));

        Assert.Equal("Friday", saved.Name);
        Assert.Equal("Friday", h.Events[saved.Id.ToString("D")].GetString("Name"));
    }

    [Fact]
    public async Task UnknownIdsStill404BeforeTheBodyIsRead()
    {
        // A missing row is a 404 even when the body is also bad: the lookup comes first, as before.
        Assert.IsType<NotFoundResult>(await h.GameApi.Update(TestRequests.Raw("{"), Guid.NewGuid(), default));
        Assert.IsType<NotFoundResult>(await h.ActivityApi.UpdateComments(TestRequests.Raw("{", "text/plain"), Guid.NewGuid(), default));
        Assert.IsType<NotFoundResult>(await h.EventApi.Update(TestRequests.Raw("null"), Guid.NewGuid(), default));
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
