using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MW_GC.EventManager.API.Functions;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;

namespace MW_GC.EventManager.Api.Tests;

[TestClass]
public class IdempotentCreateTests
{
    private readonly ConcurrentDictionary<string, TableEntity> rows = new();
    private readonly EventFunctions api;
    private readonly EventEntity input;
    private Func<Task>? beforeInsert;

    public IdempotentCreateTests()
    {
        var game = new Game { Name = "Game", Id = Guid.NewGuid() };
        input = new EventEntity
        {
            Name = "Retry me", Date = DateTimeOffset.Parse("2026-09-21T19:00:00Z"),
            Selections = [new Selection { Game = game, Activity = new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Activity" } }]
        };
        var table = new Mock<TableClient>();
        table.Setup(t => t.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>()))
            .Returns(async (TableEntity row, CancellationToken _) =>
            {
                if (beforeInsert is not null) await beforeInsert();
                if (!rows.TryAdd(row.RowKey, new TableEntity(row))) throw new RequestFailedException(409, "Entity already exists", "EntityAlreadyExists", null);
                return Mock.Of<Response>();
            });
        table.Setup(t => t.UpsertEntityAsync(It.IsAny<TableEntity>(), TableUpdateMode.Replace, It.IsAny<CancellationToken>()))
            .Callback<TableEntity, TableUpdateMode, CancellationToken>((row, _, _) => rows[row.RowKey] = new TableEntity(row))
            .ReturnsAsync(Mock.Of<Response>());
        table.Setup(t => t.GetEntityAsync<TableEntity>(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string id, IEnumerable<string> _, CancellationToken _) => rows.TryGetValue(id, out var row)
                ? Response.FromValue(row, Mock.Of<Response>()) : throw new RequestFailedException(404, "Missing"));
        table.Setup(t => t.QueryAsync(It.IsAny<Expression<Func<TableEntity, bool>>>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(() => AsyncPageable<TableEntity>.FromPages([Page<TableEntity>.FromValues(rows.Values.ToList(), null, Mock.Of<Response>())]));
        var service = new Mock<TableServiceClient>();
        service.Setup(s => s.GetTableClient(It.IsAny<string>())).Returns(table.Object);
        api = new(new(service.Object, "events", "events"), new(service.Object, "games", "games"), new(service.Object, "activities", "activities"), new(), Microsoft.Extensions.Logging.Abstractions.NullLogger<EventFunctions>.Instance);
    }

    [TestMethod]
    public async Task LostCreateResponseThenRepeatedRetryReturnsOnePersistedEvent()
    {
        var key = Guid.NewGuid().ToString("D");
        await api.SaveCustomized(Request(input, key), default); // Committed response never reaches client.
        for (var retry = 0; retry < 3; retry++)
            Assert.IsExactInstanceOfType<OkObjectResult>(await api.SaveCustomized(Request(input, key), default));
        Assert.AreEqual(Guid.Parse(key), Assert.ContainsSingle(await Listed()).Id);
    }

    [TestMethod]
    public async Task ConcurrentSameKeyInsertsReconcileWithoutOverwriting()
    {
        var arrivals = 0;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        beforeInsert = async () =>
        {
            if (Interlocked.Increment(ref arrivals) == 2) barrier.SetResult();
            await barrier.Task.WaitAsync(TimeSpan.FromSeconds(10));
        };
        var key = Guid.NewGuid().ToString("D");
        var results = await Task.WhenAll(api.SaveCustomized(Request(input, key), default), api.SaveCustomized(Request(input, key), default));
        Assert.ContainsSingle(results.OfType<CreatedResult>());
        Assert.ContainsSingle(results.OfType<OkObjectResult>());
        Assert.AreEqual(Guid.Parse(key), Assert.ContainsSingle(await Listed()).Id);
    }

    [TestMethod]
    [DataRow("name")]
    [DataRow("date")]
    [DataRow("snapshot")]
    [DataRow("unique")]
    public async Task ChangedRetryConflictsInsteadOfDiscardingDetails(string change)
    {
        var key = Guid.NewGuid().ToString("D");
        await api.SaveCustomized(Request(input, key), default);
        var before = JsonSerializer.Serialize(Assert.ContainsSingle(await Listed()));
        switch (change)
        {
            case "name": input.Name = "Changed"; break;
            case "date": input.Date = input.Date.AddHours(1); break;
            case "snapshot": input.Selections[0] = input.Selections[0] with { Activity = input.Selections[0].Activity with { Comments = "Changed" } }; break;
            case "unique": input.UniqueGamesOnly = false; break;
        }
        Assert.IsExactInstanceOfType<ConflictObjectResult>(await api.SaveCustomized(Request(input, key), default));
        Assert.AreEqual(before, JsonSerializer.Serialize(Assert.ContainsSingle(await Listed())));
    }

    [TestMethod]
    public async Task RetryIgnoresStorageMetadataButNeverOverwritesLaterUpdate()
    {
        var key = Guid.NewGuid().ToString("D");
        await api.SaveCustomized(Request(input, key), default);
        rows[key].Timestamp = DateTimeOffset.UtcNow;
        rows[key].ETag = new ETag("storage-version");
        input.Date = input.Date.ToOffset(TimeSpan.FromHours(3));
        Assert.IsExactInstanceOfType<OkObjectResult>(await api.SaveCustomized(Request(input, key), default));
        var saved = Assert.ContainsSingle(await Listed());
        saved.Name = "Later edit";
        Assert.IsExactInstanceOfType<OkObjectResult>(await api.Update(Request(saved), saved.Id, default));
        Assert.IsExactInstanceOfType<ConflictObjectResult>(await api.SaveCustomized(Request(input, key), default));
        Assert.AreEqual("Later edit", Assert.ContainsSingle(await Listed()).Name);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("bad-key")]
    [DataRow("00000000-0000-0000-0000-000000000000")]
    [DataRow("11111111111111111111111111111111")]
    public async Task InvalidKeyDoesNotCreate(string key)
    {
        Assert.IsExactInstanceOfType<BadRequestObjectResult>(await api.SaveCustomized(Request(input, key), default));
        Assert.IsEmpty(await Listed());
    }

    [TestMethod]
    public async Task LegacyClientsStillCreateIndependentEvents()
    {
        await api.SaveCustomized(Request(input), default);
        await api.SaveCustomized(Request(input), default);
        Assert.AreEqual(2, (await Listed()).Select(e => e.Id).Distinct().Count());
    }

    private static HttpRequest Request(EventEntity entity, string? key = null)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddOptions().BuildServiceProvider();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(entity));
        if (key is not null) context.Request.Headers["Idempotency-Key"] = key;
        return context.Request;
    }

    private async Task<List<EventEntity>> Listed() => Assert.IsExactInstanceOfType<List<EventEntity>>(
        Assert.IsExactInstanceOfType<OkObjectResult>(await api.GetAll(Request(input), default)).Value);
}
