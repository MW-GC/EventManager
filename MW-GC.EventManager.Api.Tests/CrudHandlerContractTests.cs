using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// One behaviour per test, run once per entity type through each Functions class's own trigger
/// shim (#59): the Game, Theme, Holiday and Activity routes share one CRUD handler, so the same
/// failure must give the same status and body on every one of them.
/// </summary>
[TestClass]
public class CrudHandlerContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly LibraryHarness h = new();
    private readonly GameEntity game;

    public CrudHandlerContractTests() => game = h.SeedGame("Owner");

    public static IEnumerable<object?[]> Kinds => [["game"], ["theme"], ["holiday"], ["activity"]];

    // ---- shims: every call goes through the kind's own Functions class

    private object Body(string kind, string name) =>
        kind == "activity" ? new { name, gameId = game.Id } : new { name };

    private static string Plural(string kind) => kind == "activity" ? "activities" : kind + "s";

    private static HttpRequest WithIfMatch(HttpRequest request, string? ifMatch)
    {
        if (ifMatch is not null) request.Headers["If-Match"] = ifMatch;
        return request;
    }

    private Task<IActionResult> Post(string kind, HttpRequest request) => kind switch
    {
        "game" => h.GameApi.Create(request, default),
        "theme" => h.ThemeApi.Create(request, default),
        "holiday" => h.HolidayApi.Create(request, default),
        "activity" => h.ActivityApi.Create(request, default),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private Task<IActionResult> Put(string kind, Guid id, HttpRequest request) => kind switch
    {
        "game" => h.GameApi.Update(request, id, default),
        "theme" => h.ThemeApi.Update(request, id, default),
        "holiday" => h.HolidayApi.Update(request, id, default),
        "activity" => h.ActivityApi.Update(request, id, default),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private Task<IActionResult> Delete(string kind, Guid id, HttpRequest request) => kind switch
    {
        "game" => h.GameApi.Delete(request, id, default),
        "theme" => h.ThemeApi.Delete(request, id, default),
        "holiday" => h.HolidayApi.Delete(request, id, default),
        "activity" => h.ActivityApi.Delete(request, id, default),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static string? ETagHeader(HttpRequest request) =>
        request.HttpContext.Response.Headers.ETag.Count == 0 ? null : request.HttpContext.Response.Headers.ETag.ToString();

    private async Task<(Guid Id, string ETag)> Create(string kind, string name = "Original")
    {
        var request = TestRequests.Json(Body(kind, name));
        var result = await Post(kind, request);
        Assert.AreEqual(StatusCodes.Status201Created, ApiResults.Status(result));
        return (Assert.IsInstanceOfType<EntityBase>(Assert.IsInstanceOfType<ObjectResult>(result).Value).Id, ETagHeader(request)!);
    }

    // ---- create

    [TestMethod]
    [DynamicData(nameof(Kinds))]
    public async Task ValidCreateIs201WithLocationAndETag(string kind)
    {
        var request = TestRequests.Json(Body(kind, "Made"));

        var result = await Post(kind, request);

        var created = Assert.IsExactInstanceOfType<CreatedResult>(result);
        Assert.AreEqual(StatusCodes.Status201Created, created.StatusCode);
        var entity = Assert.IsInstanceOfType<EntityBase>(created.Value);
        Assert.AreEqual($"/api/{Plural(kind)}/{entity.Id}", created.Location);
        var etag = ETagHeader(request);
        Assert.IsNotNull(etag, "A create must answer with the new row's ETag header.");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(created.Value, Web));
        Assert.AreEqual(etag, json.RootElement.GetProperty("eTag").GetString());
        Assert.AreEqual("Made", json.RootElement.GetProperty("name").GetString());
    }

    [TestMethod]
    [DynamicData(nameof(Kinds))]
    public async Task CreateWithATruncatedBodyIsTheSame400AndWritesNothing(string kind)
    {
        var result = await Post(kind, TestRequests.Raw("{\"name\": \"Trunc"));

        Assert.IsExactInstanceOfType<BadRequestObjectResult>(result);
        Assert.AreEqual(StatusCodes.Status400BadRequest, ApiResults.Status(result));
        Assert.AreEqual(RequestBody.InvalidJsonMessage, ApiResults.Message(result));
        Assert.AreEqual(0, h.Writes());
    }

    [TestMethod]
    [DynamicData(nameof(Kinds))]
    public async Task CreateWithAnEmptyNameIsTheSame400AndWritesNothing(string kind)
    {
        var result = await Post(kind, TestRequests.Json(Body(kind, "   ")));

        Assert.IsExactInstanceOfType<BadRequestObjectResult>(result);
        Assert.AreEqual("Name is required.", ApiResults.Message(result));
        Assert.AreEqual(0, h.Writes());
    }

    // ---- update

    [TestMethod]
    [DynamicData(nameof(Kinds))]
    public async Task UpdateOfAnUnknownIdIs404AndWritesNothing(string kind)
    {
        await Create(kind);
        var writes = h.Writes();
        var request = TestRequests.Json(Body(kind, "Ghost"));

        var result = await Put(kind, Guid.NewGuid(), request);

        Assert.IsExactInstanceOfType<NotFoundResult>(result);
        Assert.AreEqual(writes, h.Writes());
        Assert.IsNull(ETagHeader(request));
    }

    [TestMethod]
    [DynamicData(nameof(Kinds))]
    public async Task UpdateWithAStaleIfMatchIs409WithTheConflictMessage(string kind)
    {
        var (id, original) = await Create(kind);
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(await Put(kind, id, WithIfMatch(TestRequests.Json(Body(kind, "First")), original))));

        var result = await Put(kind, id, WithIfMatch(TestRequests.Json(Body(kind, "Second")), original));

        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(result));
        Assert.AreEqual(RouteFailures.ConflictMessage, ApiResults.Message(result));
    }

    // ---- delete

    [TestMethod]
    [DynamicData(nameof(Kinds))]
    public async Task DeleteOfAMissingRowIs204(string kind)
    {
        Assert.IsExactInstanceOfType<NoContentResult>(await Delete(kind, Guid.NewGuid(), TestRequests.Empty()));
        Assert.IsExactInstanceOfType<NoContentResult>(await Delete(kind, Guid.NewGuid(), WithIfMatch(TestRequests.Empty(), "*")));
    }

    [TestMethod]
    [DynamicData(nameof(Kinds))]
    public async Task DeleteWithAStaleIfMatchIs409WithTheConflictMessage(string kind)
    {
        var (id, original) = await Create(kind);
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(await Put(kind, id, WithIfMatch(TestRequests.Json(Body(kind, "Newer")), original))));

        var result = await Delete(kind, id, WithIfMatch(TestRequests.Empty(), original));

        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(result));
        Assert.AreEqual(RouteFailures.ConflictMessage, ApiResults.Message(result));
    }
}
