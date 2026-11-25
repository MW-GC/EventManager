using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;
using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Moq;
using MW_GC.EventManager.API.Functions;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Shared.Requests;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>A clock that answers one fixed instant until a test moves it.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
}

/// <summary>
/// The Series routes and the Event routes over in-memory tables (#204): Events, Series, Comments and Event
/// metrics, behind the real <see cref="TableStore{TEntity}"/>, with a fixed clock. Every read goes through
/// the routes, so a test sees what a client sees.
/// </summary>
internal sealed class SeriesHarness
{
    /// <summary>The first Occurrence of the weekly Series the tests create: Thursday 4 March 2027, 19:00 at -05:00.</summary>
    public const string First = "2027-03-04T19:00:00-05:00";

    /// <summary>The clock the tests start at: the morning of the first Occurrence.</summary>
    public const string Morning = "2027-03-04T12:00:00-05:00";

    public static readonly TimeSpan Offset = TimeSpan.FromHours(-5);

    public ConcurrentDictionary<string, TableEntity> Events { get; } = new();
    public ConcurrentDictionary<string, TableEntity> SeriesRows { get; } = new();
    public ConcurrentDictionary<string, TableEntity> Metrics { get; } = new();
    public ConcurrentDictionary<string, ITableEntity> CommentRows { get; } = new();

    public Mock<TableClient> EventTable { get; }
    public Mock<TableClient> SeriesTable { get; }

    public FixedClock Clock { get; } = new(SeriesTestSupport.At(Morning));
    public SeriesFunctions SeriesApi { get; }
    public EventFunctions EventApi { get; }

    /// <summary>The Occurrence service over the same tables, for the overlapping-run test.</summary>
    public SeriesOccurrences Occurrences { get; }

    /// <summary>The Series store over the same table, for the storage tests.</summary>
    public TableStore<SeriesEntity> SeriesStore { get; }

    /// <summary>Runs once, with the row key, right before the next Event delete reaches the table.</summary>
    public Func<string, Task>? BeforeEventDelete { get; set; }

    /// <summary>Runs once, with the row key, right before the next Event update reaches the table.</summary>
    public Func<string, Task>? BeforeEventUpdate { get; set; }

    /// <summary>Runs once right before the next read of the whole Event table.</summary>
    public Func<Task>? BeforeEventQuery { get; set; }

    /// <summary>
    /// Asked before every Event insert, update and delete (<c>add</c>, <c>update</c> or <c>delete</c>, and the row
    /// key): true fails that write with a 503 and writes nothing, as a process that died there leaves the tables.
    /// </summary>
    public Func<string, string, bool>? FailEventWrite { get; set; }

    /// <summary>
    /// Lists the Event rows oldest first instead of in the dictionary's order (storage lists them by row key, an
    /// order that has nothing to do with their dates), so a test can say which Occurrence an edit reaches first.
    /// </summary>
    public bool ListEventsByDate { get; set; }

    public SeriesHarness()
    {
        EventTable = TableMocks.Table(Events);
        // Event writes behave as storage does (409 when an insert finds the row, 404 when gone, 412 when If-Match
        // is stale), with hooks that run first, so a test can land a concurrent write between a route's read and
        // its write, or stop the route at one write.
        EventTable.Setup(t => t.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>()))
            .Returns((TableEntity row, CancellationToken _) =>
            {
                Fail("add", row.RowKey);
                return Events.TryAdd(row.RowKey, row)
                    ? Task.FromResult(Stamp(Events, row))
                    : throw new RequestFailedException(409, "Entity already exists", "EntityAlreadyExists", null);
            });
        EventTable.Setup(t => t.UpdateEntityAsync(It.IsAny<TableEntity>(), It.IsAny<ETag>(), TableUpdateMode.Replace, It.IsAny<CancellationToken>()))
            .Returns(async (TableEntity row, ETag ifMatch, TableUpdateMode _, CancellationToken _) =>
            {
                if (BeforeEventUpdate is { } hook)
                {
                    BeforeEventUpdate = null; // once
                    await hook(row.RowKey);
                }
                Fail("update", row.RowKey);
                Check(Events, row.RowKey, ifMatch);
                return Stamp(Events, row);
            });
        EventTable.Setup(t => t.DeleteEntityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ETag>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string id, ETag ifMatch, CancellationToken _) =>
            {
                if (BeforeEventDelete is { } hook)
                {
                    BeforeEventDelete = null; // once
                    await hook(id);
                }
                Fail("delete", id);
                Check(Events, id, ifMatch);
                Events.TryRemove(id, out _);
                return new StorageResponse();
            });
        EventTable.Setup(t => t.QueryAsync(It.IsAny<Expression<Func<TableEntity, bool>>>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (BeforeEventQuery is { } hook)
                {
                    BeforeEventQuery = null; // once
                    hook().GetAwaiter().GetResult();
                }
                var rows = Events.Values.ToList();
                if (ListEventsByDate) rows = rows.OrderBy(r => r.GetDateTimeOffset("Date")).ToList();
                return AsyncPageable<TableEntity>.FromPages([Page<TableEntity>.FromValues(rows, null, Mock.Of<Response>())]);
            });
        SeriesTable = TableMocks.Table(SeriesRows);
        var service = new Mock<TableServiceClient>();
        service.Setup(s => s.GetTableClient(TableNames.Events)).Returns(EventTable.Object);
        service.Setup(s => s.GetTableClient(TableNames.Series)).Returns(SeriesTable.Object);
        service.Setup(s => s.GetTableClient(TableNames.EventMetrics)).Returns(TableMocks.Table(Metrics).Object);
        service.Setup(s => s.GetTableClient(TableNames.Comments)).Returns(TableMocks.PartitionedTable<CommentEntity>(CommentRows).Object);
        service.Setup(s => s.GetTableClient(TableNames.Games)).Returns(TableMocks.Table(new ConcurrentDictionary<string, GameEntity>()).Object);
        service.Setup(s => s.GetTableClient(TableNames.Activities)).Returns(TableMocks.Table(new ConcurrentDictionary<string, TableEntity>()).Object);

        var events = new TableStore<EventEntity>(service.Object, TableNames.Events, TableNames.EventPartition);
        var series = new TableStore<SeriesEntity>(service.Object, TableNames.Series, TableNames.SeriesPartition);
        var comments = new TableStore<CommentEntity>(service.Object, TableNames.Comments, TableNames.CommentPartition);
        var metrics = new TableStore<EventMetricsEntity>(service.Object, TableNames.EventMetrics, TableNames.EventMetricsPartition);
        SeriesStore = series;
        Occurrences = new SeriesOccurrences(events, comments, metrics);
        SeriesApi = new SeriesFunctions(series, events, comments, metrics, new ListLogger<SeriesFunctions>()) { Clock = Clock };
        EventApi = new EventFunctions(events,
            new TableStore<GameEntity>(service.Object, TableNames.Games, TableNames.GamePartition),
            new TableStore<ActivityEntity>(service.Object, TableNames.Activities, TableNames.ActivityPartition),
            metrics, series, new ListLogger<EventFunctions>());
    }

    private void Fail(string write, string rowKey)
    {
        if (FailEventWrite?.Invoke(write, rowKey) == true)
            throw new RequestFailedException(503, "The server is busy.", "ServerBusy", null);
    }

    // A conditional write to the row under that key: 404 when there is none, 412 when If-Match is stale.
    private static void Check(ConcurrentDictionary<string, TableEntity> rows, string key, ETag ifMatch)
    {
        if (!rows.TryGetValue(key, out var current))
            throw new RequestFailedException(404, "The specified resource does not exist.", "ResourceNotFound", null);
        if (ifMatch != ETag.All && ifMatch != current.ETag)
            throw new RequestFailedException(412, "The update condition specified in the request was not satisfied.", "UpdateConditionNotSatisfied", null);
    }

    private static Response Stamp(ConcurrentDictionary<string, TableEntity> rows, TableEntity row)
    {
        row.ETag = TableMocks.NextETag();
        rows[row.RowKey] = row;
        return new StorageResponse(row.ETag);
    }

    // ---- requests

    /// <summary>A Series body as a client sends it. <paramref name="rule"/> is the JSON <c>rule</c> object.</summary>
    public static object Body(string name = "Thursday night", string first = First, object? rule = null, string? end = null,
        int slotCount = 3, bool uniqueGamesOnly = true) => new
    {
        name,
        schedule = new { first, rule = rule ?? new { kind = "Weekly" }, end },
        slotCount,
        uniqueGamesOnly,
        filters = new { themedOnly = false, gameIds = Array.Empty<Guid>(), themeIds = Array.Empty<Guid>(), holidayIds = Array.Empty<Guid>() },
    };

    public static HttpRequest Request(object body, string? ifMatch = null, string? query = null, Guid? key = null)
    {
        var request = TestRequests.Json(body);
        return Decorate(request, ifMatch, query, key);
    }

    public static HttpRequest Raw(string json, Guid key) => Decorate(TestRequests.Raw(json), null, null, key);

    /// <summary>A hand-built request with a query string, for a body the anonymous-object helpers cannot write.</summary>
    public static HttpRequest Decorated(HttpRequest request, string query) => Decorate(request, null, query, null);

    public static HttpRequest Bare(string? ifMatch = null, string? query = null) => Decorate(TestRequests.Empty(), ifMatch, query, null);

    private static HttpRequest Decorate(HttpRequest request, string? ifMatch, string? query, Guid? key)
    {
        if (ifMatch is not null) request.Headers["If-Match"] = ifMatch;
        if (query is not null) request.QueryString = new QueryString(query);
        if (key is not null) request.Headers["Idempotency-Key"] = key.Value.ToString("D");
        return request;
    }

    public static string? ETagHeader(HttpRequest request) =>
        request.HttpContext.Response.Headers.ETag.Count == 0 ? null : request.HttpContext.Response.Headers.ETag.ToString();

    // ---- the routes

    /// <summary>Creates a Series that must be created (201); answers its id, the change and the ETag header.</summary>
    public async Task<(Guid Id, SeriesChange Change, string ETag)> CreateAsync(object? body = null, Guid? key = null)
    {
        var request = Request(body ?? Body(), key: key ?? Guid.NewGuid());
        var result = await SeriesApi.Create(request, default);
        Assert.AreEqual(StatusCodes.Status201Created, ApiResults.Status(result));
        var change = ApiResults.Value<SeriesChange>(result);
        return (change.Series!.Id, change, ETagHeader(request)!);
    }

    public async Task<SeriesEntity?> SeriesAsync(Guid id)
    {
        var result = await SeriesApi.Get(Bare(), id, default);
        return ApiResults.Status(result) == StatusCodes.Status404NotFound ? null : ApiResults.Value<SeriesEntity>(result);
    }

    /// <summary>Every Event, as <c>GET /api/events</c> lists them, oldest first.</summary>
    public async Task<List<EventEntity>> EventsAsync() =>
        ApiResults.Value<List<EventEntity>>(await EventApi.GetAll(Bare(), default)).OrderBy(e => e.Date).ToList();

    /// <summary>The Occurrences of one Series, oldest first.</summary>
    public async Task<List<EventEntity>> OccurrencesAsync(Guid seriesId) =>
        (await EventsAsync()).Where(e => e.SeriesId == seriesId).ToList();

    /// <summary>An Event's date as the tests write instants: <c>2027-03-04T19:00:00-05:00</c>.</summary>
    public static string Local(DateTimeOffset instant) =>
        instant.ToOffset(Offset).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    public static string[] Dates(IEnumerable<EventEntity> events) => events.Select(e => Local(e.Date)).ToArray();

    // ---- what makes an Occurrence played, written straight into the tables

    public void SeedComment(Guid eventId)
    {
        var comment = new CommentEntity
        {
            Id = Guid.NewGuid(), ActivityId = Guid.NewGuid(), Text = "Good night", CreatedUtc = Clock.Now, EventId = eventId,
            PartitionKey = TableNames.CommentPartition, ETag = TableMocks.NextETag(),
        };
        CommentRows[TableMocks.Key(comment.PartitionKey, comment.RowKey)] = comment;
    }

    public void SeedMetrics(Guid eventId, int? attendees = 12)
    {
        var id = eventId.ToString("D");
        Metrics[id] = new TableEntity(TableNames.EventMetricsPartition, id) { ["Attendees"] = attendees, ETag = TableMocks.NextETag() };
    }

    /// <summary>
    /// Gives a stored Occurrence one Selection and that Selection as its Winner, written straight into the row
    /// as a roll that is not a manual edit would (so it is NOT Modified, and only the Winner protects it).
    /// </summary>
    public void SeedWinner(Guid eventId)
    {
        var game = new Game { Id = Guid.NewGuid(), Name = "Alpha" };
        var activity = new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Raid" };
        var row = Events[eventId.ToString("D")];
        row["Selections"] = System.Text.Json.JsonSerializer.Serialize(new[] { new Selection { Game = game, Activity = activity } },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        row["WinnerActivityId"] = activity.Id;
    }

    /// <summary>Makes an Occurrence played for one reason: <c>winner</c>, <c>comment</c> or <c>metrics</c>.</summary>
    public void MakePlayed(Guid eventId, string reason)
    {
        switch (reason)
        {
            case "winner": SeedWinner(eventId); break;
            case "comment": SeedComment(eventId); break;
            case "metrics": SeedMetrics(eventId); break;
            default: throw new ArgumentOutOfRangeException(nameof(reason), reason, null);
        }
    }

    /// <summary>Creates a standalone Event with one Selection through <c>POST /api/events</c>.</summary>
    public async Task<EventEntity> CreateStandaloneAsync(string name = "Solo")
    {
        var game = new Game { Id = Guid.NewGuid(), Name = "Alpha" };
        var body = new
        {
            name, date = "2027-03-20T19:00:00-05:00", uniqueGamesOnly = true,
            selections = new[] { new Selection { Game = game, Activity = new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Raid" } } },
        };
        var result = await EventApi.SaveCustomized(Request(body), default);
        Assert.AreEqual(StatusCodes.Status201Created, ApiResults.Status(result));
        return ApiResults.Value<EventEntity>(result);
    }

    /// <summary>Renames an Occurrence by hand through <c>PUT /api/events/{id}</c> (the "this occurrence" scope).</summary>
    public async Task<EventEntity> EditOneAsync(EventEntity occurrence, string name)
    {
        var body = new { name, date = occurrence.Date, uniqueGamesOnly = occurrence.UniqueGamesOnly, selections = Array.Empty<object>() };
        var result = await EventApi.Update(Request(body), occurrence.Id, default);
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(result));
        return ApiResults.Value<EventEntity>(result);
    }

    /// <summary>A Series edit that must succeed (200); answers the change.</summary>
    public async Task<SeriesChange> EditAsync(Guid id, object body, string query, string? ifMatch = null)
    {
        var result = await SeriesApi.Update(Request(body, ifMatch, query), id, default);
        Assert.AreEqual(StatusCodes.Status200OK, ApiResults.Status(result), result is Microsoft.AspNetCore.Mvc.ObjectResult o ? o.Value?.ToString() : null);
        return ApiResults.Value<SeriesChange>(result);
    }
}
