using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// The Game, Theme, Holiday and Activity handlers, and the Event gaps (#56), through the real
/// Functions classes over in-memory tables. Null bodies, deletes, stale If-Match and in-use
/// refusals are covered by ApiValidationRouteTests, ConditionalWriteTests and
/// DeleteInUseRefusalTests; this class adds only what those do not pin.
/// </summary>
[TestClass]
public class LibraryHandlerTests
{
    private readonly LibraryHarness h = new();
    private readonly GameEntity game;

    public LibraryHandlerTests() => game = h.SeedGame("Owner");

    public static IEnumerable<object?[]> LibraryKinds => [["game"], ["theme"], ["holiday"], ["activity"]];

    public static IEnumerable<object?[]> AllKinds => [.. LibraryKinds, ["event"]];

    // ---- helpers

    // A body for the kind; "id" is sent only when given (JSON null cannot bind to a Guid Id).
    private Dictionary<string, object> Body(string kind, string name, Guid? id = null)
    {
        var body = new Dictionary<string, object> { ["name"] = name };
        if (id is { } value) body["id"] = value;
        switch (kind)
        {
            case "activity":
                body["gameId"] = game.Id;
                break;
            case "event":
                body["date"] = "2026-10-02T18:00:00Z";
                body["uniqueGamesOnly"] = true;
                body["selections"] = new[] { new { game = new { id = game.Id, name = "Owner" }, activity = new { id = Guid.NewGuid(), gameId = game.Id, name = "Act" } } };
                break;
        }
        return body;
    }

    private static string Plural(string kind) => kind == "activity" ? "activities" : kind + "s";

    private Task<IActionResult> Post(string kind, HttpRequest request) => kind switch
    {
        "game" => h.GameApi.Create(request, default),
        "theme" => h.ThemeApi.Create(request, default),
        "holiday" => h.HolidayApi.Create(request, default),
        "activity" => h.ActivityApi.Create(request, default),
        "event" => h.EventApi.SaveCustomized(request, default),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private Task<IActionResult> Put(string kind, Guid id, HttpRequest request) => kind switch
    {
        "game" => h.GameApi.Update(request, id, default),
        "theme" => h.ThemeApi.Update(request, id, default),
        "holiday" => h.HolidayApi.Update(request, id, default),
        "activity" => h.ActivityApi.Update(request, id, default),
        "event" => h.EventApi.Update(request, id, default),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static EntityBase Entity(IActionResult result) =>
        Assert.IsInstanceOfType<EntityBase>(Assert.IsInstanceOfType<ObjectResult>(result).Value);

    private async Task<Guid> Create(string kind, string name)
    {
        var result = await Post(kind, TestRequests.Json(Body(kind, name)));
        Assert.AreEqual(StatusCodes.Status201Created, ApiResults.Status(result));
        return Entity(result).Id;
    }

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

    private int Rows(string kind) => kind switch
    {
        "game" => h.Games.Count,
        "theme" => h.Themes.Count,
        "holiday" => h.Holidays.Count,
        "activity" => h.Activities.Count,
        "event" => h.Events.Count,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    // ---- create

    [TestMethod]
    [DynamicData(nameof(LibraryKinds))]
    public async Task CreateAssignsANewServerIdAndAnswers201WithItsLocation(string kind)
    {
        var clientId = Guid.NewGuid();
        var rowsBefore = Rows(kind);

        var result = await Post(kind, TestRequests.Json(Body(kind, "Made", clientId)));

        var created = Assert.IsExactInstanceOfType<CreatedResult>(result);
        Assert.AreEqual(StatusCodes.Status201Created, created.StatusCode);
        var id = Entity(result).Id;
        Assert.AreNotEqual(clientId, id, "The server must not keep the Id the client sent.");
        Assert.AreNotEqual(Guid.Empty, id);
        Assert.AreEqual($"/api/{Plural(kind)}/{id}", created.Location);
        Assert.AreEqual("Made", StoredName(kind, id));
        Assert.IsNull(StoredName(kind, clientId));
        Assert.AreEqual(rowsBefore + 1, Rows(kind));
    }

    // ---- update

    [TestMethod]
    [DynamicData(nameof(AllKinds))]
    public async Task UpdateWithAnUnknownIdIs404AndLeavesTheRowItsBodyNamesAlone(string kind)
    {
        // The body names a row that exists; the route id does not. The route id decides.
        var existing = await Create(kind, "Original");
        var unknown = Guid.NewGuid();
        var writes = h.Writes();
        var rows = Rows(kind);

        var result = await Put(kind, unknown, TestRequests.Json(Body(kind, "Hijacked", existing)));

        Assert.IsExactInstanceOfType<NotFoundResult>(result);
        Assert.AreEqual("Original", StoredName(kind, existing));
        Assert.IsNull(StoredName(kind, unknown), "An update must never create the row.");
        Assert.AreEqual(rows, Rows(kind));
        Assert.AreEqual(writes, h.Writes());
    }

    [TestMethod]
    [DynamicData(nameof(LibraryKinds))]
    public async Task UpdateWritesTheRouteIdAndIgnoresTheBodyId(string kind)
    {
        var target = await Create(kind, "Target");
        var other = await Create(kind, "Other");
        var rows = Rows(kind);

        var result = await Put(kind, target, TestRequests.Json(Body(kind, "Renamed", other)));

        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(result));
        Assert.AreEqual(target, Entity(result).Id);
        Assert.AreEqual("Renamed", StoredName(kind, target));
        Assert.AreEqual("Other", StoredName(kind, other));
        Assert.AreEqual(rows, Rows(kind));
    }

    // ---- Activity duplicate and comments

    [TestMethod]
    public async Task DuplicateIs201WithANewIdACopyNameAndEqualButSeparateLists()
    {
        Guid[] themes = [Guid.NewGuid(), Guid.NewGuid()];
        Guid[] holidays = [Guid.NewGuid()];
        var source = ApiResults.Value<ActivityEntity>(await h.ActivityApi.Create(TestRequests.Json(new
        {
            gameId = game.Id, name = "Capture the Flag", description = "Two teams", rules = "No camping",
            themeIds = themes, holidayIds = holidays, setupRequirements = "Big map", comments = "Fan favourite",
        }), default));

        var result = await h.ActivityApi.Duplicate(TestRequests.Empty(), source.Id, default);

        var created = Assert.IsExactInstanceOfType<CreatedResult>(result);
        Assert.AreEqual(StatusCodes.Status201Created, created.StatusCode);
        var copy = Assert.IsExactInstanceOfType<ActivityEntity>(created.Value);
        Assert.AreNotEqual(source.Id, copy.Id);
        Assert.AreNotEqual(Guid.Empty, copy.Id);
        Assert.AreEqual($"/api/activities/{copy.Id}", created.Location);
        Assert.AreEqual("Capture the Flag (Copy)", copy.Name);
        Assert.AreEqual(source.GameId, copy.GameId);
        Assert.AreEqual(source.Description, copy.Description);
        Assert.AreEqual(source.Rules, copy.Rules);
        Assert.AreEqual(source.SetupRequirements, copy.SetupRequirements);
        Assert.AreEqual(source.Comments, copy.Comments);
        Assert.AreSequenceEqual(themes, copy.ThemeIds);
        Assert.AreSequenceEqual(holidays, copy.HolidayIds);
        Assert.AreEqual(2, h.Activities.Count);

        // Separate: emptying the copy's lists leaves the original's lists as they were.
        var cleared = await h.ActivityApi.Update(TestRequests.Json(new
        {
            gameId = game.Id, name = copy.Name, themeIds = Array.Empty<Guid>(), holidayIds = Array.Empty<Guid>(),
        }), copy.Id, default);
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(cleared));

        var original = ApiResults.Value<ActivityEntity>(await h.ActivityApi.Get(TestRequests.Empty(), source.Id, default));
        Assert.AreEqual("Capture the Flag", original.Name);
        Assert.AreSequenceEqual(themes, original.ThemeIds);
        Assert.AreSequenceEqual(holidays, original.HolidayIds);
        var readCopy = ApiResults.Value<ActivityEntity>(await h.ActivityApi.Get(TestRequests.Empty(), copy.Id, default));
        Assert.IsEmpty(readCopy.ThemeIds);
        Assert.IsEmpty(readCopy.HolidayIds);
    }

    [TestMethod]
    public async Task CommentsPatchChangesOnlyTheComments()
    {
        Guid[] themes = [Guid.NewGuid()];
        Guid[] holidays = [Guid.NewGuid(), Guid.NewGuid()];
        var created = ApiResults.Value<ActivityEntity>(await h.ActivityApi.Create(TestRequests.Json(new
        {
            gameId = game.Id, name = "Relay", description = "Pass it on", rules = "One lap each",
            themeIds = themes, holidayIds = holidays, setupRequirements = "Track", comments = "Old note",
        }), default));
        var key = created.Id.ToString("D");
        string[] untouched = ["GameId", "Name", "Description", "Rules", "ThemeIds", "HolidayIds", "SetupRequirements"];
        var before = untouched.ToDictionary(name => name, name => h.Activities[key][name]);

        // Other fields in the PATCH body are not part of the comments payload and must be ignored.
        var result = await h.ActivityApi.UpdateComments(
            TestRequests.Json(new { comments = "Bring snacks", name = "Ignored", rules = "Ignored", themeIds = Array.Empty<Guid>() }),
            created.Id, default);

        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(result));
        var patched = ApiResults.Value<ActivityEntity>(result);
        Assert.AreEqual("Bring snacks", patched.Comments);
        Assert.AreEqual("Relay", patched.Name);
        Assert.AreEqual("One lap each", patched.Rules);
        Assert.AreSequenceEqual(themes, patched.ThemeIds);

        Assert.AreEqual("Bring snacks", h.Activities[key].GetString("Comments"));
        foreach (var name in untouched)
            Assert.AreEqual(before[name], h.Activities[key][name], $"{name} changed.");
        Assert.ContainsSingle(h.Activities);
    }

    // ---- Event

    [TestMethod]
    public async Task EventGetWithAnUnknownIdIs404()
    {
        await Create("event", "Friday");
        var request = TestRequests.Empty();

        var result = await h.EventApi.Get(request, Guid.NewGuid(), default);

        Assert.IsExactInstanceOfType<NotFoundResult>(result);
        Assert.AreEqual(0, request.HttpContext.Response.Headers.ETag.Count);
    }
}
