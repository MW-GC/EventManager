using Microsoft.AspNetCore.Http;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>The Game URL fields through the real Game routes: the length caps, and the Website http(s) rule on create and update (#127).</summary>
[TestClass]
public class GameFieldRouteTests
{
    private readonly LibraryHarness h = new();

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

    // ---- Game Website: an http or https address when given (#127), on create and update alike.

    [TestMethod]
    [DataRow("ftp://x")]
    [DataRow("javascript:alert(1)")]
    [DataRow("JavaScript:alert(1)")]
    [DataRow(" javascript:alert(1)")]
    [DataRow("data:text/html,x")]
    [DataRow("example.com")]
    public async Task NonHttpWebsiteIs400OnCreateAndUpdateAndNothingIsStored(string website)
    {
        var game = h.SeedGame("Alpha");
        game.Website = "https://alpha.test";

        var create = await h.GameApi.Create(TestRequests.Json(new { name = "Beta", website }), default);
        var update = await h.GameApi.Update(TestRequests.Json(new { name = "Alpha", website }), game.Id, default);

        foreach (var result in new[] { create, update })
        {
            Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(result));
            Assert.AreEqual(ApiMessages.Website, ApiResults.Message(result));
        }
        Assert.AreEqual("https://alpha.test", Assert.ContainsSingle(h.Games).Value.Website);
        Assert.AreEqual(0, h.Writes());
    }

    [TestMethod]
    [DataRow("http://x")]
    [DataRow("HTTPS://x")]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow(null)]
    public async Task HttpOrEmptyWebsiteIsAcceptedOnCreateAndUpdate(string? website)
    {
        var game = h.SeedGame("Alpha");

        var create = await h.GameApi.Create(TestRequests.Json(new { name = "Beta", website }), default);
        var update = await h.GameApi.Update(TestRequests.Json(new { name = "Alpha", website }), game.Id, default);

        Assert.AreEqual(StatusCodes.Status201Created, ApiResults.Status(create));
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(update));
        var created = ApiResults.Value<GameEntity>(create);
        Assert.AreEqual(website, created.Website);
        Assert.AreEqual(website, h.Games[created.RowKey].Website);
        Assert.AreEqual(website, h.Games[game.RowKey].Website);
    }

    // A Website stored before the rule still loads; only a save checks it.
    [TestMethod]
    public async Task OldStoredWebsiteWithoutAnHttpSchemeIsStillReturnedByGet()
    {
        var old = h.SeedGame("Old");
        old.Website = "example.com";

        var one = await h.GameApi.Get(TestRequests.Empty(), old.Id, default);
        var all = await h.GameApi.GetAll(TestRequests.Empty(), default);

        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(one));
        Assert.AreEqual("example.com", ApiResults.Value<GameEntity>(one).Website);
        Assert.AreEqual("example.com", Assert.ContainsSingle(ApiResults.Value<List<GameEntity>>(all)).Website);
    }

    [TestMethod]
    public async Task OldStoredWebsiteIsCheckedWhenTheGameIsSavedAgain()
    {
        var old = h.SeedGame("Old");
        old.Website = "example.com";

        var unchanged = await h.GameApi.Update(TestRequests.Json(new { name = "Renamed", website = "example.com" }), old.Id, default);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(unchanged));
        Assert.AreEqual(ApiMessages.Website, ApiResults.Message(unchanged));
        Assert.AreEqual(0, h.Writes());
        // Nothing was written: the stored Game, read back through the GET route, keeps its old name and Website.
        var stored = ApiResults.Value<GameEntity>(await h.GameApi.Get(TestRequests.Empty(), old.Id, default));
        Assert.AreEqual("Old", stored.Name);
        Assert.AreEqual("example.com", stored.Website);

        var fixedUp = await h.GameApi.Update(TestRequests.Json(new { name = "Renamed", website = "https://example.com" }), old.Id, default);

        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(fixedUp));
        Assert.AreEqual("https://example.com", h.Games[old.RowKey].Website);
        Assert.AreEqual("Renamed", h.Games[old.RowKey].Name);
    }
}
