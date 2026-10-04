using System.Collections.Concurrent;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Moq;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// <see cref="TableStore{TEntity}"/> called directly (#56), with no Functions class in between,
/// over in-memory tables that behave like Table Storage.
/// </summary>
[TestClass]
public class TableStoreTests
{
    private readonly ConcurrentDictionary<string, TableEntity> eventRows = new();
    private readonly ConcurrentDictionary<string, GameEntity> gameRows = new();
    private readonly Mock<TableClient> eventTable;
    private readonly Mock<TableClient> gameTable;
    private readonly TableStore<EventEntity> events;
    private readonly TableStore<GameEntity> games;

    public TableStoreTests()
    {
        eventTable = TableMocks.FilteringTable(eventRows);
        gameTable = TableMocks.FilteringTable(gameRows);
        var service = new Mock<TableServiceClient>();
        service.Setup(s => s.GetTableClient("Events")).Returns(eventTable.Object);
        service.Setup(s => s.GetTableClient("Games")).Returns(gameTable.Object);
        events = new TableStore<EventEntity>(service.Object, "Events", "Event");
        games = new TableStore<GameEntity>(service.Object, "Games", "Game");
    }

    private static EventEntity TwoSelectionEvent()
    {
        var alpha = new Game { Id = Guid.NewGuid(), Name = "Alpha", Website = "https://alpha.test" };
        var beta = new Game { Id = Guid.NewGuid(), Name = "Beta" };
        return new EventEntity
        {
            Id = Guid.NewGuid(),
            Name = "Friday",
            Date = new DateTimeOffset(2026, 10, 2, 18, 0, 0, TimeSpan.FromHours(2)),
            UniqueGamesOnly = true,
            Selections =
            [
                new Selection { Game = alpha, Activity = new Activity { Id = Guid.NewGuid(), GameId = alpha.Id, Name = "Capture", ThemeIds = [Guid.NewGuid()], Comments = "Loud" } },
                new Selection { Game = beta, Activity = new Activity { Id = Guid.NewGuid(), GameId = beta.Id, Name = "Race", HolidayIds = [Guid.NewGuid()] } },
            ],
        };
    }

    private static readonly JsonSerializerOptions Compare = new(JsonSerializerDefaults.Web);

    private static string Snapshot(EventEntity evt) => JsonSerializer.Serialize(new
    {
        evt.Id, evt.Name, evt.Date, evt.Selections, evt.UniqueGamesOnly, evt.WinnerActivityId,
    }, Compare);

    [TestMethod]
    public async Task AnEventWithSelectionsAndAWinnerRoundTripsThroughGetAndGetAll()
    {
        var input = TwoSelectionEvent();
        input.WinnerActivityId = input.Selections[1].Activity.Id;

        Assert.IsTrue(await events.TryAddAsync(input));

        // Stored as a flat row: the partition set by the store, Selections as camelCase JSON, the Winner as a Guid.
        var row = eventRows[input.RowKey];
        Assert.AreEqual("Event", row.PartitionKey);
        Assert.AreEqual(input.WinnerActivityId, row.GetGuid("WinnerActivityId"));
        var json = row.GetString("Selections")!;
        Assert.Contains("\"activity\":", json);
        Assert.DoesNotContain("\"Activity\":", json);
        Assert.AreNotEqual(default, input.ETag, "The add must report the row's new ETag.");

        var read = await events.GetAsync(input.Id);
        Assert.IsNotNull(read);
        Assert.AreEqual(Snapshot(input), Snapshot(read));
        Assert.AreEqual(input.ETag, read.ETag);
        Assert.AreEqual("Event", read.PartitionKey);

        var listed = Assert.ContainsSingle(await events.GetAllAsync());
        Assert.AreEqual(Snapshot(input), Snapshot(listed));
    }

    [TestMethod]
    public async Task LegacyPascalCaseSelectionsJsonHydrates()
    {
        // Written by an earlier version of the app with the serializer defaults: PascalCase names.
        var old = TwoSelectionEvent();
        old.WinnerActivityId = old.Selections[0].Activity.Id;
        var legacy = JsonSerializer.Serialize(old.Selections);
        Assert.Contains("\"Activity\":", legacy);
        Assert.Contains("\"GameId\":", legacy);
        eventRows[old.RowKey] = new TableEntity("Event", old.RowKey)
        {
            ["Name"] = old.Name, ["Date"] = old.Date, ["Selections"] = legacy,
            ["UniqueGamesOnly"] = old.UniqueGamesOnly, ["WinnerActivityId"] = old.WinnerActivityId,
        };

        var read = await events.GetAsync(old.Id);
        var listed = Assert.ContainsSingle(await events.GetAllAsync());

        Assert.IsNotNull(read);
        Assert.AreEqual(Snapshot(old), Snapshot(read));
        Assert.AreEqual(Snapshot(old), Snapshot(listed));
        Assert.AreEqual("Alpha", read.Selections[0].Game.Name);
        Assert.AreEqual(old.Selections[0].Activity.ThemeIds[0], read.Selections[0].Activity.ThemeIds[0]);
    }

    [TestMethod]
    public async Task GetOfAMissingRowIsNullForSimpleAndComplexEntities()
    {
        Assert.IsNull(await events.GetAsync(Guid.NewGuid()));
        Assert.IsNull(await games.GetAsync(Guid.NewGuid()));
    }

    [TestMethod]
    public async Task DeleteOfAMissingRowIsMissingWhetherStorageThrowsOrAnswers404()
    {
        // The in-memory table throws a 404, as a conditional delete does.
        Assert.AreEqual(DeleteOutcome.Missing, await games.DeleteAsync(Guid.NewGuid()));

        // The SDK can also hand the 404 back as a response instead of throwing it.
        gameTable.Setup(t => t.DeleteEntityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ETag>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response>(r => r.Status == 404));
        Assert.AreEqual(DeleteOutcome.Missing, await games.DeleteAsync(Guid.NewGuid()));
    }

    [TestMethod]
    public async Task DeleteOfAStoredRowIsDeletedAndTheRowIsGone()
    {
        var game = new GameEntity { Name = "Alpha" };
        await games.UpsertAsync(game);

        Assert.AreEqual(DeleteOutcome.Deleted, await games.DeleteAsync(game.Id, game.ETag));
        Assert.IsEmpty(gameRows);
        Assert.IsNull(await games.GetAsync(game.Id));
    }

    [TestMethod]
    public async Task AddOfAnExistingRowIsFalseAndKeepsTheStoredRow()
    {
        var first = TwoSelectionEvent();
        Assert.IsTrue(await events.TryAddAsync(first));
        var stored = eventRows[first.RowKey];

        var again = TwoSelectionEvent();
        again.Id = first.Id;
        again.Name = "Second";

        Assert.IsFalse(await events.TryAddAsync(again));
        Assert.AreSame(stored, eventRows[first.RowKey]);
        Assert.AreEqual("Friday", (await events.GetAsync(first.Id))!.Name);
    }

    [TestMethod]
    [DataRow("UpdateConditionNotSatisfied")]
    [DataRow("TableBeingDeleted")]
    [DataRow(null)]
    public async Task AnAdd409OtherThanEntityAlreadyExistsIsNotSwallowed(string? errorCode)
    {
        var error = new RequestFailedException(409, "Conflict", errorCode, null);
        eventTable.Setup(t => t.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>())).ThrowsAsync(error);

        var thrown = await Assert.ThrowsAsync<RequestFailedException>(() => events.TryAddAsync(TwoSelectionEvent()));

        Assert.AreSame(error, thrown);
        Assert.IsEmpty(eventRows);
    }

    [TestMethod]
    public async Task GetAllReturnsOnlyTheStoresPartition()
    {
        // One table, two partitions: only the store's own partition may come back.
        var mine = TwoSelectionEvent();
        Assert.IsTrue(await events.TryAddAsync(mine));
        var foreignKey = Guid.NewGuid().ToString("D");
        eventRows[foreignKey] = new TableEntity("Other", foreignKey) { ["Name"] = "Foreign", ["Selections"] = "[]" };

        var ownGame = new GameEntity { Name = "Mine" };
        await games.UpsertAsync(ownGame);
        var foreignGame = new GameEntity { Name = "Foreign", PartitionKey = "Other" };
        gameRows[foreignGame.RowKey] = foreignGame;

        var listedEvents = await events.GetAllAsync();
        var listedGames = await games.GetAllAsync();

        Assert.AreEqual(mine.Id, Assert.ContainsSingle(listedEvents).Id);
        Assert.AreEqual(ownGame.Id, Assert.ContainsSingle(listedGames).Id);
        Assert.AreEqual(2, eventRows.Count);
        Assert.AreEqual(2, gameRows.Count);
    }
}
