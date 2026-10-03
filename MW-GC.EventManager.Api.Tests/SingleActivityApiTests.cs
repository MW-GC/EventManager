using System.Linq.Expressions;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MW_GC.EventManager.API.Functions;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;

namespace MW_GC.EventManager.Api.Tests;

[TestClass]
public class SingleActivityApiTests
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TableEntity> rows = new();
    private bool failBeforeInsert;
    private bool failAfterInsert;
    private Func<Task>? beforeInsert;
    private readonly List<GameEntity> gameRows = [];
    private readonly List<TableEntity> activityRows = [];
    private readonly EventFunctions functions;
    private readonly Game game = new() { Id = Guid.NewGuid(), Name = "Game" };
    private readonly Activity activity;

    public SingleActivityApiTests()
    {
        activity = new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Activity" };
        var table = new Mock<TableClient>();
        table.Setup(t => t.UpsertEntityAsync(It.IsAny<TableEntity>(), TableUpdateMode.Replace, It.IsAny<CancellationToken>()))
            .Callback<TableEntity, TableUpdateMode, CancellationToken>((row, _, _) => rows[row.RowKey] = new TableEntity(row))
            .ReturnsAsync(TableMocks.Written);
        // #46: an Event PUT is a conditional update of an existing row, no longer an upsert.
        table.Setup(t => t.UpdateEntityAsync(It.IsAny<TableEntity>(), It.IsAny<ETag>(), TableUpdateMode.Replace, It.IsAny<CancellationToken>()))
            .Returns((TableEntity row, ETag _, TableUpdateMode _, CancellationToken _) =>
            {
                if (!rows.ContainsKey(row.RowKey)) throw new RequestFailedException(404, "Missing", "ResourceNotFound", null);
                rows[row.RowKey] = new TableEntity(row);
                return Task.FromResult(TableMocks.Written());
            });
        table.Setup(t => t.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>()))
            .Returns(async (TableEntity row, CancellationToken _) =>
            {
                if (beforeInsert is not null) await beforeInsert();
                if (failBeforeInsert) throw new RequestFailedException(503, "Storage unavailable");
                if (!rows.TryAdd(row.RowKey, new TableEntity(row)))
                    throw new RequestFailedException(409, "Entity already exists", "EntityAlreadyExists", null);
                if (failAfterInsert) throw new RequestFailedException(503, "Insert response lost");
                return TableMocks.Written();
            });
        table.Setup(t => t.GetEntityAsync<TableEntity>(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string id, IEnumerable<string> _, CancellationToken _) => Response.FromValue(rows[id], Mock.Of<Response>()));
        table.Setup(t => t.QueryAsync(It.IsAny<Expression<Func<TableEntity, bool>>>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(() => AsyncPageable<TableEntity>.FromPages([Page<TableEntity>.FromValues(rows.Values.ToList(), null, Mock.Of<Response>())]));
        var service = new Mock<TableServiceClient>();
        service.Setup(s => s.GetTableClient(It.IsAny<string>())).Returns(table.Object);
        var games = new Mock<TableClient>();
        gameRows.Add(new GameEntity { Id = game.Id });
        games.Setup(t => t.QueryAsync(It.IsAny<Expression<Func<GameEntity, bool>>>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(() => AsyncPageable<GameEntity>.FromPages([Page<GameEntity>.FromValues(gameRows, null, Mock.Of<Response>())]));
        var activities = new Mock<TableClient>();
        activityRows.Add(new TableEntity("activities", activity.Id.ToString()) { ["GameId"] = game.Id });
        activities.Setup(t => t.QueryAsync(It.IsAny<Expression<Func<TableEntity, bool>>>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(() => AsyncPageable<TableEntity>.FromPages([Page<TableEntity>.FromValues(activityRows, null, Mock.Of<Response>())]));
        service.Setup(s => s.GetTableClient("games")).Returns(games.Object);
        service.Setup(s => s.GetTableClient("activities")).Returns(activities.Object);
        functions = new EventFunctions(new(service.Object, "events", "events"), new(service.Object, "games", "games"), new(service.Object, "activities", "activities"), Microsoft.Extensions.Logging.Abstractions.NullLogger<EventFunctions>.Instance);
    }

    private EventEntity Event(params Activity[] activities) => new()
    {
        Name = "Single roll", Date = new DateTimeOffset(2026, 9, 21, 18, 0, 0, TimeSpan.Zero), UniqueGamesOnly = false,
        Selections = activities.Select(a => new Selection { Game = game, Activity = a }).ToList()
    };

    private static HttpRequest Request<T>(T value)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddOptions().BuildServiceProvider();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(value));
        return context.Request;
    }

    private async Task<EventEntity> Read(Guid id) => Assert.IsExactInstanceOfType<EventEntity>(Assert.IsExactInstanceOfType<OkObjectResult>(await functions.Get(Request(new { }), id, default)).Value);

    private async Task AssertPersisted(EventEntity expected)
    {
        var row = rows[expected.Id.ToString()];
        Assert.AreEqual(expected.WinnerActivityId, row.TryGetValue("WinnerActivityId", out var winner) ? winner : null);
        var detail = await Read(expected.Id);
        var listed = Assert.IsExactInstanceOfType<List<EventEntity>>(Assert.IsExactInstanceOfType<OkObjectResult>(await functions.GetAll(Request(new { }), default)).Value);
        foreach (var actual in new[] { detail, Assert.ContainsSingle(listed) })
        {
            Assert.AreEqual(expected.Id, actual.Id);
            Assert.AreEqual(expected.Name, actual.Name);
            Assert.AreEqual(expected.Date, actual.Date);
            Assert.AreEqual(expected.UniqueGamesOnly, actual.UniqueGamesOnly);
            Assert.AreSequenceEqual(expected.Selections.Select(s => s.Activity.Id), actual.Selections.Select(s => s.Activity.Id));
            Assert.AreEqual(expected.WinnerActivityId, actual.WinnerActivityId);
        }
    }

    [TestMethod]
    public async Task RetryingCreateAfterLostResponseReturnsSameEventWithoutOverwriting()
    {
        var input = Event(activity);
        var key = Guid.NewGuid().ToString("D");
        HttpRequest CreateRequest()
        {
            var request = Request(input);
            request.Headers["Idempotency-Key"] = key;
            return request;
        }

        // The create commits, but its response never reaches the client.
        var first = Assert.IsExactInstanceOfType<CreatedResult>(await functions.SaveCustomized(CreateRequest(), default));
        var saved = Assert.IsExactInstanceOfType<EventEntity>(first.Value);
        var retry = Assert.IsExactInstanceOfType<OkObjectResult>(await functions.SaveCustomized(CreateRequest(), default));
        Assert.AreEqual(saved.Id, Assert.IsExactInstanceOfType<EventEntity>(retry.Value).Id);
        await AssertPersisted(saved);

        input.Name = "Changed during retry";
        Assert.IsExactInstanceOfType<ConflictObjectResult>(await functions.SaveCustomized(CreateRequest(), default));
        await AssertPersisted(saved);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StorageFailureBeforeOrAfterCommitCanBeRetried(bool committed)
    {
        var input = Event(activity);
        var key = Guid.NewGuid();
        HttpRequest CreateRequest()
        {
            var request = Request(input);
            request.Headers["Idempotency-Key"] = key.ToString("D");
            return request;
        }
        failBeforeInsert = !committed;
        failAfterInsert = committed;
        // #45: a storage outage is now logged and answered with a 503, not thrown to the host.
        Assert.AreEqual(503, Assert.IsExactInstanceOfType<ObjectResult>(await functions.SaveCustomized(CreateRequest(), default)).StatusCode);
        failBeforeInsert = failAfterInsert = false;
        var result = Assert.IsInstanceOfType<ObjectResult>(await functions.SaveCustomized(CreateRequest(), default));
        Assert.AreEqual(committed ? 200 : 201, result.StatusCode);
        var saved = Assert.IsExactInstanceOfType<EventEntity>(result.Value);
        Assert.AreEqual(key, saved.Id);
        await AssertPersisted(saved);
    }

    [TestMethod]
    public async Task ConcurrentCreatesWithSameKeyInsertOnlyOnce()
    {
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        beforeInsert = () =>
        {
            if (Interlocked.Increment(ref arrivals) == 2) bothEntered.SetResult();
            return bothEntered.Task;
        };
        var input = Event(activity);
        var key = Guid.NewGuid().ToString("D");
        HttpRequest CreateRequest()
        {
            var request = Request(input);
            request.Headers["Idempotency-Key"] = key;
            return request;
        }
        var results = await Task.WhenAll(
            functions.SaveCustomized(CreateRequest(), default),
            functions.SaveCustomized(CreateRequest(), default)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.ContainsSingle(results.OfType<CreatedResult>());
        Assert.ContainsSingle(results.OfType<OkObjectResult>());
        var saved = Assert.IsExactInstanceOfType<EventEntity>(results.OfType<CreatedResult>().Single().Value);
        await AssertPersisted(saved);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("not-a-guid")]
    [DataRow("00000000-0000-0000-0000-000000000000")]
    [DataRow("efb26a9120f848dfb67b104c4096da81")]
    public async Task InvalidCreateKeysAreRejectedWithoutWriting(string key)
    {
        var request = Request(Event(activity));
        request.Headers["Idempotency-Key"] = key;
        Assert.IsExactInstanceOfType<BadRequestObjectResult>(await functions.SaveCustomized(request, default));
        Assert.IsEmpty(rows);
    }

    [TestMethod]
    public async Task MultipleCreateKeysAreRejectedWithoutWriting()
    {
        var request = Request(Event(activity));
        request.Headers["Idempotency-Key"] = new Microsoft.Extensions.Primitives.StringValues([Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D")]);
        Assert.IsExactInstanceOfType<BadRequestObjectResult>(await functions.SaveCustomized(request, default));
        Assert.IsEmpty(rows);
    }

    [TestMethod]
    public async Task LegacyCreatesStillReceiveIndependentServerIds()
    {
        var input = Event(activity);
        var first = Assert.IsExactInstanceOfType<EventEntity>(Assert.IsExactInstanceOfType<CreatedResult>(await functions.SaveCustomized(Request(input), default)).Value);
        var second = Assert.IsExactInstanceOfType<EventEntity>(Assert.IsExactInstanceOfType<CreatedResult>(await functions.SaveCustomized(Request(input), default)).Value);
        Assert.AreNotEqual(input.Id, first.Id);
        Assert.AreNotEqual(first.Id, second.Id);
        var listed = Assert.IsExactInstanceOfType<List<EventEntity>>(Assert.IsExactInstanceOfType<OkObjectResult>(await functions.GetAll(Request(new { }), default)).Value);
        Assert.AreEqual(2, listed.Count);
    }

    private async Task<EventEntity> Update(EventEntity input)
    {
        // A rejected edit must fail here, not masquerade as a winner-storage regression.
        var result = Assert.IsExactInstanceOfType<OkObjectResult>(await functions.Update(Request(input), input.Id, default));
        var updated = Assert.IsExactInstanceOfType<EventEntity>(result.Value);
        await AssertPersisted(updated);
        return updated;
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CustomizedSaveOverridesStaleWinnerAndRoundTripsStorage(bool unique)
    {
        var input = Event(activity);
        input.UniqueGamesOnly = unique;
        input.WinnerActivityId = Guid.NewGuid();
        var saved = Assert.IsExactInstanceOfType<EventEntity>(Assert.IsExactInstanceOfType<CreatedResult>(await functions.SaveCustomized(Request(input), default)).Value);
        Assert.AreEqual(activity.Id, saved.WinnerActivityId);
        await AssertPersisted(saved);
        Assert.AreNotEqual(Guid.Empty, saved.Id);
        Assert.AreEqual(activity.Id, rows[saved.Id.ToString()]["WinnerActivityId"]);
        var read = await Read(saved.Id);
        Assert.AreEqual(activity.Id, read.WinnerActivityId);
        Assert.AreEqual(activity.Id, Assert.ContainsSingle(read.Selections).Activity.Id);
    }

    [TestMethod]
    public async Task EditingReplacesSingleWinnerPreservesValidMultiWinnerAndClearsRemovedWinner()
    {
        var saved = Assert.IsExactInstanceOfType<EventEntity>(Assert.IsExactInstanceOfType<CreatedResult>(await functions.SaveCustomized(Request(Event(activity)), default)).Value);
        var replacement = new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Other" };
        saved.Selections = Event(replacement).Selections;
        saved = await Update(saved);
        saved = await Read(saved.Id);
        Assert.AreEqual(replacement.Id, saved.WinnerActivityId);
        saved.Selections.AddRange(Event(activity).Selections);
        saved = await Update(saved);
        Assert.AreEqual(replacement.Id, (await Read(saved.Id)).WinnerActivityId);
        saved.Selections.RemoveAt(0);
        saved.Selections.AddRange(Event(new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Other" }).Selections);
        saved = await Update(saved);
        Assert.IsNull((await Read(saved.Id)).WinnerActivityId);
    }

    [TestMethod]
    public async Task MultipleSelectionsDoNotAutomaticallyChooseWinner()
    {
        var input = Event(activity, new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Other" });
        var saved = Assert.IsExactInstanceOfType<EventEntity>(Assert.IsExactInstanceOfType<CreatedResult>(await functions.SaveCustomized(Request(input), default)).Value);
        Assert.IsNull((await Read(saved.Id)).WinnerActivityId);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ReducingMultipleSelectionsToOneReplacesRemovedOrMissingWinner(bool hadWinner)
    {
        var removed = new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Removed" };
        var input = Event(activity, removed);
        input.WinnerActivityId = hadWinner ? removed.Id : null;
        var saved = Assert.IsExactInstanceOfType<EventEntity>(Assert.IsExactInstanceOfType<CreatedResult>(await functions.SaveCustomized(Request(input), default)).Value);
        saved.Selections.RemoveAt(1);
        saved.Name = "Reduced event";
        saved = await Update(saved);
        Assert.ContainsSingle(saved.Selections);
        Assert.AreEqual(activity.Id, saved.WinnerActivityId);
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("tooMany")]
    [DataRow("nullSelections")]
    [DataRow("nullSelection")]
    [DataRow("nullActivity")]
    [DataRow("nullGame")]
    [DataRow("emptyActivityId")]
    [DataRow("emptyGameId")]
    [DataRow("mismatchedGame")]
    [DataRow("duplicateActivity")]
    [DataRow("duplicateGame")]
    public async Task InvalidCustomizedCreateAndUpdateDoNotWrite(string invalid)
    {
        var saved = Assert.IsExactInstanceOfType<EventEntity>(Assert.IsExactInstanceOfType<CreatedResult>(await functions.SaveCustomized(Request(Event(activity)), default)).Value);
        var original = rows[saved.Id.ToString()];
        var input = Event(activity);
        switch (invalid)
        {
            case "empty": input.Selections.Clear(); break;
            case "tooMany": input.Selections = Enumerable.Range(0, 6).Select(_ => new Selection { Game = game, Activity = new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Other" } }).ToList(); break;
            case "nullSelections": input.Selections = null!; break;
            case "nullSelection": input.Selections = [null!]; break;
            case "nullActivity": input.Selections = [new Selection { Game = game, Activity = null! }]; break;
            case "nullGame": input.Selections = [new Selection { Game = null!, Activity = activity }]; break;
            case "emptyActivityId": input.Selections = Event(activity with { Id = Guid.Empty }).Selections; break;
            case "emptyGameId": input.Selections = [new Selection { Game = game with { Id = Guid.Empty }, Activity = activity with { GameId = Guid.Empty } }]; break;
            case "mismatchedGame": input.Selections = Event(activity with { GameId = Guid.NewGuid() }).Selections; break;
            case "duplicateActivity": input.Selections.Add(input.Selections[0]); break;
            case "duplicateGame": input.UniqueGamesOnly = true; input.Selections.AddRange(Event(new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Other" }).Selections); break;
        }
        Assert.IsExactInstanceOfType<BadRequestObjectResult>(await functions.SaveCustomized(Request(input), default));
        Assert.IsExactInstanceOfType<BadRequestObjectResult>(await functions.Update(Request(input), saved.Id, default));
        Assert.ContainsSingle(rows);
        Assert.AreSame(original, rows[saved.Id.ToString()]);
        Assert.AreEqual(activity.Id, (await Read(saved.Id)).WinnerActivityId);
    }

    [TestMethod]
    public async Task FiveSelectionsAndExplicitWinnerRoundTripThroughBothReads()
    {
        var input = Event(Enumerable.Range(0, 5).Select(_ => new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Other" }).ToArray());
        input.WinnerActivityId = input.Selections[2].Activity.Id;
        var saved = Assert.IsExactInstanceOfType<EventEntity>(Assert.IsExactInstanceOfType<CreatedResult>(await functions.SaveCustomized(Request(input), default)).Value);
        Assert.AreEqual(input.WinnerActivityId, (await Read(saved.Id)).WinnerActivityId);
        var list = Assert.IsExactInstanceOfType<List<EventEntity>>(Assert.IsExactInstanceOfType<OkObjectResult>(await functions.GetAll(Request(new { }), default)).Value);
        Assert.AreEqual(input.WinnerActivityId, Assert.ContainsSingle(list).WinnerActivityId);
        saved.WinnerActivityId = saved.Selections[4].Activity.Id;
        var explicitWinner = saved.WinnerActivityId;
        saved = await Update(saved);
        Assert.AreEqual(explicitWinner, saved.WinnerActivityId);
        saved.WinnerActivityId = null;
        saved = await Update(saved);
        Assert.IsNull((await Read(saved.Id)).WinnerActivityId);
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("{")]
    public async Task InvalidCustomizedBodiesDoNotOverwriteSavedEvent(string json)
    {
        var saved = Assert.IsExactInstanceOfType<EventEntity>(Assert.IsExactInstanceOfType<CreatedResult>(await functions.SaveCustomized(Request(Event(activity)), default)).Value);
        HttpRequest Body()
        {
            var request = Request(new { });
            request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
            return request;
        }
        // #45: the body guard answers a 400 with a short message (BadRequestObjectResult).
        Assert.IsExactInstanceOfType<BadRequestObjectResult>(await functions.SaveCustomized(Body(), default));
        Assert.IsExactInstanceOfType<BadRequestObjectResult>(await functions.Update(Body(), saved.Id, default));
        Assert.ContainsSingle(rows);
        Assert.AreEqual(activity.Id, (await Read(saved.Id)).WinnerActivityId);
    }
}
