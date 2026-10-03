using System.Collections.Concurrent;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// Conflict-safe writes (#46) through the real Functions classes and TableStore over in-memory
/// tables that behave like Table Storage: every write gets a new ETag, an update or delete with a
/// stale If-Match is refused with 412, and an update of a missing row is a 404, never an insert.
/// </summary>
[TestClass]
public class ConditionalWriteTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly LibraryHarness h = new();
    private readonly GameEntity game;

    public ConditionalWriteTests()
    {
        game = h.SeedGame("Owner");
        game.ETag = TableMocks.NextETag(); // as if storage had written it
    }

    public static IEnumerable<object?[]> Kinds => [["game"], ["theme"], ["holiday"], ["activity"], ["event"]];

    public static IEnumerable<object?[]> KindsWithAndWithoutIfMatch =>
        Kinds.SelectMany(k => new object?[][] { [k[0], true], [k[0], false] });

    // ---- helpers: every row is created through the API, so it carries a real stored ETag

    private object Body(string kind, string name) => kind switch
    {
        "activity" => new { name, gameId = game.Id },
        "event" => new
        {
            name, date = "2026-10-02T18:00:00Z", uniqueGamesOnly = true,
            selections = new[] { new { game = new { id = game.Id, name = "Owner" }, activity = new { id = Guid.NewGuid(), gameId = game.Id, name = "Act" } } },
        },
        _ => new { name },
    };

    private static HttpRequest Request(object body, string? ifMatch = null)
    {
        var request = TestRequests.Json(body);
        if (ifMatch is not null) request.Headers["If-Match"] = ifMatch;
        return request;
    }

    private static HttpRequest Bare(string? ifMatch = null)
    {
        var request = TestRequests.Empty();
        if (ifMatch is not null) request.Headers["If-Match"] = ifMatch;
        return request;
    }

    private static string? ETagHeader(HttpRequest request) =>
        request.HttpContext.Response.Headers.ETag.Count == 0 ? null : request.HttpContext.Response.Headers.ETag.ToString();

    private static string? BodyETag(IActionResult result)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsInstanceOfType<ObjectResult>(result).Value, Web));
        return json.RootElement.GetProperty("eTag").GetString();
    }

    private async Task<(Guid Id, string ETag)> Create(string kind, string name = "Original")
    {
        var request = Request(Body(kind, name));
        var result = kind switch
        {
            "game" => await h.GameApi.Create(request, default),
            "theme" => await h.ThemeApi.Create(request, default),
            "holiday" => await h.HolidayApi.Create(request, default),
            "activity" => await h.ActivityApi.Create(request, default),
            "event" => await h.EventApi.SaveCustomized(request, default),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        Assert.AreEqual(StatusCodes.Status201Created, ApiResults.Status(result));
        var value = Assert.IsInstanceOfType<EntityBase>(Assert.IsInstanceOfType<ObjectResult>(result).Value);
        var etag = ETagHeader(request);
        Assert.IsNotNull(etag, "POST must answer with the new row's ETag header.");
        Assert.AreEqual(etag, BodyETag(result));
        return (value.Id, etag);
    }

    private Task<IActionResult> Put(string kind, Guid id, HttpRequest request) => kind switch
    {
        "game" => h.GameApi.Update(request, id, default),
        "theme" => h.ThemeApi.Update(request, id, default),
        "holiday" => h.HolidayApi.Update(request, id, default),
        "activity" => h.ActivityApi.Update(request, id, default),
        "event" => h.EventApi.Update(request, id, default),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private Task<IActionResult> Delete(string kind, Guid id, HttpRequest request) => kind switch
    {
        "game" => h.GameApi.Delete(request, id, default),
        "theme" => h.ThemeApi.Delete(request, id, default),
        "holiday" => h.HolidayApi.Delete(request, id, default),
        "activity" => h.ActivityApi.Delete(request, id, default),
        "event" => h.EventApi.Delete(request, id, default),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private string? StoredName(string kind, Guid id)
    {
        var key = id.ToString("D");
        return kind switch
        {
            "game" => h.Games.TryGetValue(key, out var g) ? g.Name : null,
            "theme" => h.Themes.TryGetValue(key, out var t) ? t.Name : null,
            "holiday" => h.Holidays.TryGetValue(key, out var x) ? x.Name : null,
            "activity" => h.Activities.TryGetValue(key, out var a) ? a.GetString("Name") : null,
            "event" => h.Events.TryGetValue(key, out var e) ? e.GetString("Name") : null,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    private Mock<TableClient> TableOf(string kind) => kind switch
    {
        "game" => h.GameTable,
        "theme" => h.ThemeTable,
        "holiday" => h.HolidayTable,
        "activity" => h.ActivityTable,
        "event" => h.EventTable,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static int Calls(Mock<TableClient> table, string method) => table.Invocations.Count(i => i.Method.Name == method);

    // A delete that lands between the route's existence check and its write: the read still
    // returns the row, and the row is gone by the time the write reaches storage.
    private static void VanishAfterRead<T>(Mock<TableClient> table, ConcurrentDictionary<string, T> rows) where T : class, ITableEntity, new() =>
        table.Setup(t => t.GetEntityAsync<T>(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string id, IEnumerable<string> _, CancellationToken _) => rows.TryRemove(id, out var row)
                ? Response.FromValue(row, Mock.Of<Response>())
                : throw new RequestFailedException(404, "Missing"));

    private void VanishAfterRead(string kind)
    {
        switch (kind)
        {
            case "game": VanishAfterRead(h.GameTable, h.Games); break;
            case "theme": VanishAfterRead(h.ThemeTable, h.Themes); break;
            case "holiday": VanishAfterRead(h.HolidayTable, h.Holidays); break;
            case "activity": VanishAfterRead(h.ActivityTable, h.Activities); break;
            case "event": VanishAfterRead(h.EventTable, h.Events); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    // ---- PUT

    [TestMethod]
    [DynamicData(nameof(Kinds))]
    public async Task PutWithAStaleIfMatchIs409AndLeavesTheRowUnchanged(string kind)
    {
        var (id, original) = await Create(kind);

        // Another admin saves first: the row now has a newer ETag.
        var first = Request(Body(kind, "First editor"), original);
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(await Put(kind, id, first)));
        Assert.AreNotEqual(original, ETagHeader(first));

        // The second admin still holds the original ETag.
        var result = await Put(kind, id, Request(Body(kind, "Second editor"), original));

        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(result));
        Assert.AreEqual(RouteFailures.ConflictMessage, ApiResults.Message(result));
        Assert.AreEqual("First editor", StoredName(kind, id));
        // Only a create may upsert (Events create with an insert); both PUTs were conditional updates.
        Assert.AreEqual(kind is "event" ? 0 : 1, Calls(TableOf(kind), nameof(TableClient.UpsertEntityAsync)));
        Assert.AreEqual(2, Calls(TableOf(kind), nameof(TableClient.UpdateEntityAsync)));
    }

    [TestMethod]
    [DynamicData(nameof(KindsWithAndWithoutIfMatch))]
    public async Task PutForARowDeletedAfterTheExistenceCheckIs404AndRecreatesNothing(string kind, bool withIfMatch)
    {
        var (id, etag) = await Create(kind);
        var writesBefore = h.Writes();
        VanishAfterRead(kind);

        var result = await Put(kind, id, Request(Body(kind, "Resurrected"), withIfMatch ? etag : null));

        Assert.IsExactInstanceOfType<NotFoundResult>(result);
        Assert.IsNull(StoredName(kind, id), "The deleted row must not be recreated.");
        var table = TableOf(kind);
        Assert.AreEqual(1, Calls(table, nameof(TableClient.UpdateEntityAsync)));
        Assert.AreEqual(writesBefore + 1, h.Writes(), "The only write is the refused conditional update.");
    }

    [TestMethod]
    public async Task IfMatchIsPassedToStorageAsGivenAndNoIfMatchMeansAnyExistingRow()
    {
        var (id, etag) = await Create("game");

        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(await Put("game", id, Request(Body("game", "Tagged"), etag))));
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(await Put("game", id, Request(Body("game", "Untagged")))));
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(await Put("game", id, Request(Body("game", "Star"), "*"))));

        var conditions = h.GameTable.Invocations
            .Where(i => i.Method.Name == nameof(TableClient.UpdateEntityAsync))
            .Select(i => (ETag)i.Arguments[1]!)
            .ToList();
        Assert.AreSequenceEqual(new[] { new ETag(etag), ETag.All, ETag.All }, conditions);
        Assert.AreEqual("Star", StoredName("game", id));
    }

    [TestMethod]
    public async Task AnETagInTheRequestBodyIsIgnored()
    {
        var (id, original) = await Create("game");
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(await Put("game", id, Request(Body("game", "Moved on"), original))));

        // A client that echoes a whole entity, stale eTag included, but sends no If-Match.
        var echoed = await Put("game", id, Request(new { name = "Echoed", eTag = original }));

        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(echoed));
        Assert.AreEqual("Echoed", StoredName("game", id));
    }

    // ---- PATCH comments

    [TestMethod]
    public async Task CommentsPatchWithAnInterleavedWriteIs409AndTheInterleavedValueSurvives()
    {
        var (id, _) = await Create("activity");
        var key = id.ToString("D");

        // The PATCH reads the row; before it writes, another request saves new comments.
        h.ActivityTable.Setup(t => t.GetEntityAsync<TableEntity>(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string rowKey, IEnumerable<string> _, CancellationToken _) =>
            {
                var read = new TableEntity(h.Activities[rowKey]) { ETag = h.Activities[rowKey].ETag };
                h.Activities[rowKey] = new TableEntity(read) { ["Comments"] = "Interleaved", ETag = TableMocks.NextETag() };
                return Response.FromValue(read, Mock.Of<Response>());
            });

        var result = await h.ActivityApi.UpdateComments(Request(new { comments = "Mine" }), id, default);

        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(result));
        Assert.AreEqual(RouteFailures.ConflictMessage, ApiResults.Message(result));
        Assert.AreEqual("Interleaved", h.Activities[key].GetString("Comments"));
    }

    [TestMethod]
    public async Task CommentsPatchHonoursIfMatchAndAnswersWithTheNewETag()
    {
        var (id, original) = await Create("activity");

        var first = Request(new { comments = "Bring snacks" }, original);
        var ok = await h.ActivityApi.UpdateComments(first, id, default);
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(ok));
        var fresh = ETagHeader(first);
        Assert.IsNotNull(fresh);
        Assert.AreNotEqual(original, fresh);
        Assert.AreEqual(fresh, BodyETag(ok));

        var stale = await h.ActivityApi.UpdateComments(Request(new { comments = "Overwrite" }, original), id, default);
        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(stale));
        Assert.AreEqual("Bring snacks", h.Activities[id.ToString("D")].GetString("Comments"));
    }

    // ---- DELETE

    [TestMethod]
    [DynamicData(nameof(Kinds))]
    public async Task DeleteHonoursIfMatchAndAMissingRowStaysAnIdempotent204(string kind)
    {
        var (id, original) = await Create(kind);
        var update = Request(Body(kind, "Newer"), original);
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(await Put(kind, id, update)));
        var fresh = ETagHeader(update)!;

        var stale = await Delete(kind, id, Bare(original));
        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(stale));
        Assert.AreEqual(RouteFailures.ConflictMessage, ApiResults.Message(stale));
        Assert.AreEqual("Newer", StoredName(kind, id));

        Assert.IsExactInstanceOfType<NoContentResult>(await Delete(kind, id, Bare(fresh)));
        Assert.IsNull(StoredName(kind, id));

        // Gone: a repeat delete is still 204, with or without a tag.
        Assert.IsExactInstanceOfType<NoContentResult>(await Delete(kind, id, Bare(fresh)));
        Assert.IsExactInstanceOfType<NoContentResult>(await Delete(kind, id, Bare()));
        Assert.IsExactInstanceOfType<NoContentResult>(await Delete(kind, Guid.NewGuid(), Bare("*")));
    }

    // ---- ETag exposure

    [TestMethod]
    [DataRow("game")]
    [DataRow("activity")]
    [DataRow("event")]
    public async Task GetCarriesTheETagHeaderAndBodyAndPutAnswersWithANewOne(string kind)
    {
        var (id, created) = await Create(kind);

        var get = Bare();
        var read = kind switch
        {
            "game" => await h.GameApi.Get(get, id, default),
            "activity" => await h.ActivityApi.Get(get, id, default),
            _ => await h.EventApi.Get(get, id, default),
        };
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(read));
        Assert.AreEqual(created, ETagHeader(get));
        Assert.AreEqual(created, BodyETag(read));

        var put = Request(Body(kind, "Renamed"), created);
        var updated = await Put(kind, id, put);
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(updated));
        var fresh = ETagHeader(put);
        Assert.IsNotNull(fresh);
        Assert.AreNotEqual(created, fresh);
        Assert.AreEqual(fresh, BodyETag(updated));
    }

    [TestMethod]
    public async Task EveryListedEntityCarriesItsETagInTheBody()
    {
        foreach (var kind in new[] { "game", "theme", "holiday", "activity", "event" })
            await Create(kind);

        var lists = new[]
        {
            await h.GameApi.GetAll(Bare(), default),
            await h.ThemeApi.GetAll(Bare(), default),
            await h.HolidayApi.GetAll(Bare(), default),
            await h.ActivityApi.GetAll(Bare(), default),
            await h.EventApi.GetAll(Bare(), default),
        };

        foreach (var list in lists)
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsInstanceOfType<ObjectResult>(list).Value, Web));
            Assert.IsTrue(json.RootElement.GetArrayLength() > 0);
            foreach (var item in json.RootElement.EnumerateArray())
                Assert.StartsWith("W/\"datetime'", item.GetProperty("eTag").GetString()!);
        }
    }

    [TestMethod]
    public async Task ThemeAndHolidayUpdatesAnswerWithTheNewETag()
    {
        foreach (var kind in new[] { "theme", "holiday" })
        {
            var (id, created) = await Create(kind);
            var put = Request(Body(kind, "Renamed"), created);
            var updated = await Put(kind, id, put);
            Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(updated));
            Assert.AreNotEqual(created, ETagHeader(put));
            Assert.AreEqual(ETagHeader(put), BodyETag(updated));
        }
    }
}
