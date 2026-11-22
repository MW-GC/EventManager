using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>The Activity fields through the real Activity routes: GameId, the Theme and Holiday id lists, and the text caps.</summary>
[TestClass]
public class ActivityFieldRouteTests
{
    private readonly LibraryHarness h = new();

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
    public async Task OverLongActivityTextIs400NamingTheField(string json, string field)
    {
        var game = h.SeedGame();
        var body = new Dictionary<string, object> { ["gameId"] = game.Id, ["name"] = "Act", [json] = new string('t', 2001) };

        var result = await h.ActivityApi.Create(TestRequests.Json(body), default);

        Assert.AreEqual($"{field} must be 2000 characters or fewer.", ApiResults.Message(result));
        Assert.IsEmpty(h.Activities);
    }
}
