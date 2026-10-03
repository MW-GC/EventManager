using System.Linq.Expressions;
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
/// Deleting a Game, Theme or Holiday that Activities still use (#47) is refused with 409 and a
/// short message naming the count, and nothing is deleted. An unused or missing row still gives
/// the existing 204. Runs the real Functions classes over in-memory tables.
/// </summary>
[TestClass]
public class DeleteInUseRefusalTests
{
    private readonly LibraryHarness h = new();

    public DeleteInUseRefusalTests()
    {
        // The shared table mocks do not model deletes; here a delete removes the in-memory row,
        // so "the row still exists" and "the row is gone" are both real assertions.
        RemoveOnDelete(h.GameTable, h.Games);
        RemoveOnDelete(h.ThemeTable, h.Themes);
        RemoveOnDelete(h.HolidayTable, h.Holidays);
    }

    private static void RemoveOnDelete<T>(Mock<TableClient> table, System.Collections.Concurrent.ConcurrentDictionary<string, T> rows) =>
        table.Setup(t => t.DeleteEntityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ETag>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, ETag, CancellationToken>((_, id, _, _) => rows.TryRemove(id, out _))
            .ReturnsAsync(Mock.Of<Response>());

    public static IEnumerable<object?[]> Kinds => [["game"], ["theme"], ["holiday"]];

    // ---- seeding: rows written straight into the in-memory tables, as the other route tests do

    private Guid SeedRow(string kind)
    {
        var id = Guid.NewGuid();
        switch (kind)
        {
            case "game":
                h.Games[id.ToString("D")] = new GameEntity { Id = id, Name = "Alpha", PartitionKey = "Game" };
                break;
            case "theme":
                h.Themes[id.ToString("D")] = new ThemeEntity { Id = id, Name = "Spooky", PartitionKey = "Theme" };
                break;
            case "holiday":
                h.Holidays[id.ToString("D")] = new HolidayEntity { Id = id, Name = "Yule", PartitionKey = "Holiday" };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
        return id;
    }

    private void SeedActivity(Guid gameId, IEnumerable<Guid>? themeIds = null, IEnumerable<Guid>? holidayIds = null)
    {
        var id = Guid.NewGuid().ToString("D");
        h.Activities[id] = new TableEntity("Activity", id)
        {
            ["GameId"] = gameId,
            ["Name"] = "Act",
            ["ThemeIds"] = JsonSerializer.Serialize(themeIds?.ToList() ?? []),
            ["HolidayIds"] = JsonSerializer.Serialize(holidayIds?.ToList() ?? []),
        };
    }

    // One Activity that uses the row of this kind.
    private void SeedUser(string kind, Guid id)
    {
        switch (kind)
        {
            case "game": SeedActivity(id); break;
            case "theme": SeedActivity(Guid.NewGuid(), themeIds: [Guid.NewGuid(), id]); break;
            case "holiday": SeedActivity(Guid.NewGuid(), holidayIds: [id, Guid.NewGuid()]); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    // One Activity that uses a different row of this kind (and nothing of the row under test).
    private void SeedUserOfAnother(string kind)
    {
        var other = Guid.NewGuid();
        switch (kind)
        {
            case "game": SeedActivity(other); break;
            case "theme": SeedActivity(Guid.NewGuid(), themeIds: [other]); break;
            case "holiday": SeedActivity(Guid.NewGuid(), holidayIds: [other]); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private Task<IActionResult> Delete(string kind, Guid id) => kind switch
    {
        "game" => h.GameApi.Delete(TestRequests.Empty(), id, default),
        "theme" => h.ThemeApi.Delete(TestRequests.Empty(), id, default),
        "holiday" => h.HolidayApi.Delete(TestRequests.Empty(), id, default),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private bool Exists(string kind, Guid id) => kind switch
    {
        "game" => h.Games.ContainsKey(id.ToString("D")),
        "theme" => h.Themes.ContainsKey(id.ToString("D")),
        "holiday" => h.Holidays.ContainsKey(id.ToString("D")),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private Mock<TableClient> TableOf(string kind) => kind switch
    {
        "game" => h.GameTable,
        "theme" => h.ThemeTable,
        "holiday" => h.HolidayTable,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static int Deletes(Mock<TableClient> table) =>
        table.Invocations.Count(i => i.Method.Name == nameof(TableClient.DeleteEntityAsync));

    private static string Advice(string kind) =>
        kind == "game" ? "Delete or move" : "Remove it from";

    // ---- the four cases for each kind

    [TestMethod]
    [DynamicData(nameof(Kinds))]
    public async Task UnusedRowIsDeletedWith204(string kind)
    {
        var id = SeedRow(kind);

        var result = await Delete(kind, id);

        Assert.IsExactInstanceOfType<NoContentResult>(result);
        Assert.AreEqual(1, Deletes(TableOf(kind)));
        Assert.IsFalse(Exists(kind, id));
    }

    [TestMethod]
    [DynamicData(nameof(Kinds))]
    public async Task RowUsedByOneActivityIs409NamingOneActivityAndStays(string kind)
    {
        var id = SeedRow(kind);
        SeedUser(kind, id);

        var result = await Delete(kind, id);

        Assert.IsExactInstanceOfType<ConflictObjectResult>(result);
        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(result));
        var message = ApiResults.Message(result);
        Assert.StartsWith($"This {kind} is used by 1 activity.", message);
        Assert.Contains(Advice(kind), message);
        Assert.DoesNotContain("activities", message);
        Assert.IsTrue(Exists(kind, id));
        Assert.AreEqual(0, Deletes(TableOf(kind)));
    }

    [TestMethod]
    [DynamicData(nameof(Kinds))]
    public async Task RowUsedByThreeActivitiesIs409NamingThreeAndStays(string kind)
    {
        var id = SeedRow(kind);
        SeedUser(kind, id);
        SeedUser(kind, id);
        SeedUser(kind, id);
        SeedUserOfAnother(kind);

        var result = await Delete(kind, id);

        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(result));
        var message = ApiResults.Message(result);
        Assert.AreEqual(
            kind == "game"
                ? "This game is used by 3 activities. Delete or move them first."
                : $"This {kind} is used by 3 activities. Remove it from them first.",
            message);
        Assert.IsTrue(Exists(kind, id));
        Assert.AreEqual(0, Deletes(TableOf(kind)));
        Assert.AreEqual(0, h.Writes());
    }

    [TestMethod]
    [DynamicData(nameof(Kinds))]
    public async Task MissingRowStaysAnIdempotent204(string kind)
    {
        SeedUserOfAnother(kind);

        var result = await Delete(kind, Guid.NewGuid());

        Assert.IsExactInstanceOfType<NoContentResult>(result);
    }

    [TestMethod]
    [DynamicData(nameof(Kinds))]
    public async Task ActivityUsingADifferentRowDoesNotBlockTheDelete(string kind)
    {
        var id = SeedRow(kind);
        SeedUserOfAnother(kind);
        SeedUserOfAnother(kind);

        var result = await Delete(kind, id);

        Assert.IsExactInstanceOfType<NoContentResult>(result);
        Assert.AreEqual(1, Deletes(TableOf(kind)));
        Assert.IsFalse(Exists(kind, id));
    }

    [TestMethod]
    public async Task ThemeAndHolidayIdsDoNotCrossCount()
    {
        // The same Guid used as a Theme must not block deleting a Holiday with that id, and the
        // Game id of an Activity does not count as a Theme or Holiday use.
        var shared = SeedRow("holiday");
        h.Themes[shared.ToString("D")] = new ThemeEntity { Id = shared, Name = "Spooky", PartitionKey = "Theme" };
        SeedActivity(shared, themeIds: [shared]);

        Assert.IsExactInstanceOfType<NoContentResult>(await Delete("holiday", shared));
        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(await Delete("theme", shared)));
    }

    [TestMethod]
    public async Task ActivityRemovedFromTheThemeThenLetsTheThemeGo()
    {
        var theme = SeedRow("theme");
        var game = h.SeedGame();
        var created = ApiResults.Value<ActivityEntity>(await h.ActivityApi.Create(
            TestRequests.Json(new { name = "Act", gameId = game.Id, themeIds = new[] { theme } }), default));

        Assert.AreEqual(StatusCodes.Status409Conflict, ApiResults.Status(await Delete("theme", theme)));

        var updated = await h.ActivityApi.Update(TestRequests.Json(new { name = "Act", gameId = game.Id, themeIds = Array.Empty<Guid>() }), created.Id, default);
        Assert.IsEmpty(ApiResults.Value<ActivityEntity>(updated).ThemeIds);

        Assert.IsExactInstanceOfType<NoContentResult>(await Delete("theme", theme));
        Assert.IsFalse(Exists("theme", theme));
    }

    // ---- Events keep their snapshots

    [TestMethod]
    public async Task EventKeepsItsSnapshotAfterTheGameItNamesIsDeleted()
    {
        var game = h.SeedGame("Alpha");
        var activityId = Guid.NewGuid();
        var body = new EventEntity
        {
            Name = "Friday",
            Date = new DateTimeOffset(2026, 10, 2, 18, 0, 0, TimeSpan.Zero),
            Selections =
            [
                new Selection
                {
                    Game = new Game { Id = game.Id, Name = "Alpha" },
                    Activity = new Activity { Id = activityId, GameId = game.Id, Name = "Alpha Free-for-all" },
                },
            ],
        };
        var saved = ApiResults.Value<EventEntity>(await h.EventApi.SaveCustomized(TestRequests.Json(body), default));

        // No stored Activity uses the Game, so the delete goes through.
        Assert.IsExactInstanceOfType<NoContentResult>(await Delete("game", game.Id));
        Assert.IsFalse(Exists("game", game.Id));

        var read = ApiResults.Value<EventEntity>(await h.EventApi.Get(TestRequests.Empty(), saved.Id, default));
        var selection = Assert.ContainsSingle(read.Selections);
        Assert.AreEqual(game.Id, selection.Game.Id);
        Assert.AreEqual("Alpha", selection.Game.Name);
        Assert.AreEqual(activityId, selection.Activity.Id);
        Assert.AreEqual("Alpha Free-for-all", selection.Activity.Name);
        Assert.AreEqual(activityId, read.WinnerActivityId);
    }

    // ---- storage failure while counting

    [TestMethod]
    [DynamicData(nameof(Kinds))]
    public async Task OutageWhileCountingIsTheExisting503AndDeletesNothing(string kind)
    {
        var id = SeedRow(kind);
        var outage = new RequestFailedException(503, "SECRET-INTERNAL-DETAIL at Azure.Data.Tables.TableClient.Query", "ServerBusy", null);
        h.ActivityTable.Setup(t => t.QueryAsync(It.IsAny<Expression<Func<TableEntity, bool>>>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Throws(outage);

        var result = await Delete(kind, id);

        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, ApiResults.Status(result));
        Assert.AreEqual(RouteFailures.UnavailableMessage, ApiResults.Message(result));
        Assert.IsTrue(Exists(kind, id));
        Assert.AreEqual(0, Deletes(TableOf(kind)));

        var entries = kind switch
        {
            "game" => h.GameLog.Entries,
            "theme" => h.ThemeLog.Entries,
            _ => h.HolidayLog.Entries,
        };
        var entry = Assert.ContainsSingle(entries);
        Assert.AreSame(outage, entry.Exception);
        Assert.Contains("Delete" + char.ToUpperInvariant(kind[0]) + kind[1..], entry.Message);
    }

    [TestMethod]
    public async Task UnexpectedFailureWhileCountingIsTheGeneric500WithoutDetails()
    {
        var id = SeedRow("game");
        h.ActivityTable.Setup(t => t.QueryAsync(It.IsAny<Expression<Func<TableEntity, bool>>>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("SECRET-INTERNAL-DETAIL"));

        var result = await Delete("game", id);

        Assert.AreEqual(StatusCodes.Status500InternalServerError, ApiResults.Status(result));
        var body = ApiResults.Message(result);
        Assert.AreEqual(RouteFailures.UnexpectedMessage, body);
        Assert.DoesNotContain("SECRET", body);
        Assert.IsTrue(Exists("game", id));
    }
}
