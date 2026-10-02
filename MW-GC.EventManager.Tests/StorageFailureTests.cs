using System.Linq.Expressions;
using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using MW_GC.EventManager.API.Validation;
using MW_GC.EventManager.Shared.Entities;
using Xunit;

namespace MW_GC.EventManager.Tests;

/// <summary>
/// Storage failures (#45) over a mocked Table Storage client: each one is logged through ILogger
/// and answered with a short body (409 / 413 / 503 / 500), never a stack trace or exception text.
/// The running instance cannot simulate an outage, so this is where that path is proven.
/// </summary>
public class StorageFailureTests
{
    private const string Secret = "SECRET-INTERNAL-DETAIL at Azure.Data.Tables.TableClient.UpsertEntity";

    private readonly LibraryHarness h = new();

    public static TheoryData<string, int, string> Failures => new()
    {
        { "storage500", 503, RouteFailures.UnavailableMessage },
        { "storage503", 503, RouteFailures.UnavailableMessage },
        { "transport", 503, RouteFailures.UnavailableMessage },
        { "status0", 503, RouteFailures.UnavailableMessage },
        { "timeout", 503, RouteFailures.UnavailableMessage },
        { "retriesExhausted", 503, RouteFailures.UnavailableMessage },
        { "conflict", 409, RouteFailures.ConflictMessage },
        { "entityTooLarge", 413, RouteFailures.TooLargeMessage },
        { "propertyTooLarge", 413, RouteFailures.TooLargeMessage },
        { "unexpected", 500, RouteFailures.UnexpectedMessage },
        { "storage400", 500, RouteFailures.UnexpectedMessage },
    };

    private static Exception Make(string kind) => kind switch
    {
        "storage500" => new RequestFailedException(500, Secret, "InternalError", null),
        "storage503" => new RequestFailedException(503, Secret, "ServerBusy", null),
        "transport" => new HttpRequestException(Secret),
        "status0" => new RequestFailedException(0, Secret),
        "timeout" => new TaskCanceledException(Secret),
        "retriesExhausted" => new AggregateException(Secret, new RequestFailedException(503, Secret), new HttpRequestException(Secret)),
        "conflict" => new RequestFailedException(409, Secret, "UpdateConditionNotSatisfied", null),
        "entityTooLarge" => new RequestFailedException(400, Secret, "EntityTooLarge", null),
        "propertyTooLarge" => new RequestFailedException(400, Secret, "PropertyValueTooLarge", null),
        "unexpected" => new InvalidOperationException(Secret),
        "storage400" => new RequestFailedException(400, Secret, "InvalidInput", null),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task GameCreateFailureIsLoggedAndMappedWithoutLeakingDetails(string kind, int status, string message)
    {
        var error = Make(kind);
        h.GameTable.Setup(t => t.UpsertEntityAsync(It.IsAny<GameEntity>(), TableUpdateMode.Replace, It.IsAny<CancellationToken>()))
            .ThrowsAsync(error);

        var result = await h.GameApi.Create(TestRequests.Json(new { name = "Alpha" }), default);

        Assert.Equal(status, ApiResults.Status(result));
        var body = ApiResults.Message(result);
        Assert.Equal(message, body);
        Assert.DoesNotContain("SECRET", body);
        Assert.DoesNotContain(" at ", body);

        var entry = Assert.Single(h.GameLog.Entries);
        Assert.Same(error, entry.Exception);
        Assert.Contains("CreateGame", entry.Message);
        Assert.Equal(status is 409 or 413 ? LogLevel.Warning : LogLevel.Error, entry.Level);
    }

    [Fact]
    public async Task ReadFailureOnEveryEntityIsLoggedAnd503()
    {
        var outage = new RequestFailedException(503, Secret);
        h.GameTable.Setup(t => t.QueryAsync(It.IsAny<Expression<Func<GameEntity, bool>>>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Throws(outage);
        h.ThemeTable.Setup(t => t.QueryAsync(It.IsAny<Expression<Func<ThemeEntity, bool>>>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Throws(outage);
        h.HolidayTable.Setup(t => t.QueryAsync(It.IsAny<Expression<Func<HolidayEntity, bool>>>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Throws(outage);
        h.ActivityTable.Setup(t => t.QueryAsync(It.IsAny<Expression<Func<TableEntity, bool>>>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Throws(outage);
        h.EventTable.Setup(t => t.QueryAsync(It.IsAny<Expression<Func<TableEntity, bool>>>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Throws(outage);

        Assert.Equal(503, ApiResults.Status(await h.GameApi.GetAll(TestRequests.Empty(), default)));
        Assert.Equal(503, ApiResults.Status(await h.ThemeApi.GetAll(TestRequests.Empty(), default)));
        Assert.Equal(503, ApiResults.Status(await h.HolidayApi.GetAll(TestRequests.Empty(), default)));
        Assert.Equal(503, ApiResults.Status(await h.ActivityApi.GetAll(TestRequests.Empty(), default)));
        Assert.Equal(503, ApiResults.Status(await h.EventApi.GetAll(TestRequests.Empty(), default)));

        Assert.Same(outage, Assert.Single(h.GameLog.Entries).Exception);
        Assert.Same(outage, Assert.Single(h.ThemeLog.Entries).Exception);
        Assert.Same(outage, Assert.Single(h.HolidayLog.Entries).Exception);
        Assert.Same(outage, Assert.Single(h.ActivityLog.Entries).Exception);
        Assert.Same(outage, Assert.Single(h.EventLog.Entries).Exception);
    }

    [Fact]
    public async Task ActivityGameLookupOutageIs503NotGameNotFound()
    {
        h.GameTable.Setup(t => t.GetEntityAsync<GameEntity>(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(503, Secret));

        var result = await h.ActivityApi.Create(TestRequests.Json(new { name = "Act", gameId = Guid.NewGuid() }), default);

        Assert.Equal(503, ApiResults.Status(result));
        Assert.Single(h.ActivityLog.Entries);
        Assert.Empty(h.Activities);
    }

    [Fact]
    public async Task TheCallersOwnCancellationIsNotTurnedIntoAResponse()
    {
        using var cts = new CancellationTokenSource();
        h.GameTable.Setup(t => t.UpsertEntityAsync(It.IsAny<GameEntity>(), TableUpdateMode.Replace, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cts.Cancel();
                return Task.FromException<Response>(new TaskCanceledException("caller went away"));
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.GameApi.Create(TestRequests.Json(new { name = "Alpha" }), cts.Token));
        Assert.Empty(h.GameLog.Entries);
    }

    [Fact]
    public async Task EventIdempotentCreateConflictKeepsItsOwnMessage()
    {
        // The EntityAlreadyExists path is the idempotency flow, handled inside the route: its own
        // 409 message (or a 200 replay) wins, and nothing is logged as a failure.
        var game = h.SeedGame();
        var key = Guid.NewGuid().ToString("D");
        var body = new
        {
            name = "Friday", date = "2026-10-02T18:00:00Z", uniqueGamesOnly = true,
            selections = new[] { new { game = new { id = game.Id, name = "Alpha" }, activity = new { id = Guid.NewGuid(), gameId = game.Id, name = "Act" } } },
        };
        HttpRequest Create(object value)
        {
            var request = TestRequests.Json(value);
            request.Headers["Idempotency-Key"] = key;
            return request;
        }

        Assert.Equal(201, ApiResults.Status(await h.EventApi.SaveCustomized(Create(body), default)));
        Assert.Equal(200, ApiResults.Status(await h.EventApi.SaveCustomized(Create(body), default)));
        var changed = await h.EventApi.SaveCustomized(Create(body with { name = "Changed" }), default);

        Assert.IsType<ConflictObjectResult>(changed);
        Assert.Contains("different details", ApiResults.Message(changed));
        Assert.Empty(h.EventLog.Entries);
    }
}
