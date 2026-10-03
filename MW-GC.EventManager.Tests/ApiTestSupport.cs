using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using MW_GC.EventManager.API.Functions;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.Shared.Entities;
using Xunit;

namespace MW_GC.EventManager.Tests;

/// <summary>Records every log entry so a test can assert what was logged, at which level, with which exception.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, exception, formatter(state, exception)));
}

/// <summary>HTTP requests as the Functions host hands them to a route.</summary>
internal static class TestRequests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static HttpRequest Raw(string body, string? contentType = "application/json")
    {
        var context = new DefaultHttpContext();
        if (contentType is not null) context.Request.ContentType = contentType;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return context.Request;
    }

    public static HttpRequest Json<T>(T value, string? contentType = "application/json") =>
        Raw(JsonSerializer.Serialize(value, Web), contentType);

    public static HttpRequest Empty() => new DefaultHttpContext().Request;
}

/// <summary>In-memory Table Storage tables behind the real <see cref="TableStore{TEntity}"/>.</summary>
internal static class TableMocks
{
    public static Mock<TableClient> Table<T>(ConcurrentDictionary<string, T> rows) where T : class, ITableEntity, new()
    {
        var table = new Mock<TableClient>();
        table.Setup(t => t.UpsertEntityAsync(It.IsAny<T>(), TableUpdateMode.Replace, It.IsAny<CancellationToken>()))
            .Callback<T, TableUpdateMode, CancellationToken>((row, _, _) => rows[row.RowKey] = row)
            .ReturnsAsync(Mock.Of<Response>());
        table.Setup(t => t.AddEntityAsync(It.IsAny<T>(), It.IsAny<CancellationToken>()))
            .Returns((T row, CancellationToken _) => rows.TryAdd(row.RowKey, row)
                ? Task.FromResult(Mock.Of<Response>())
                : throw new RequestFailedException(409, "Entity already exists", "EntityAlreadyExists", null));
        table.Setup(t => t.GetEntityAsync<T>(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string id, IEnumerable<string> _, CancellationToken _) => rows.TryGetValue(id, out var row)
                ? Response.FromValue(row, Mock.Of<Response>())
                : throw new RequestFailedException(404, "Missing"));
        table.Setup(t => t.QueryAsync(It.IsAny<Expression<Func<T, bool>>>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(() => AsyncPageable<T>.FromPages([Page<T>.FromValues(rows.Values.ToList(), null, Mock.Of<Response>())]));
        return table;
    }
}

/// <summary>Every Functions class over one in-memory library, each with a recording logger.</summary>
internal sealed class LibraryHarness
{
    public ConcurrentDictionary<string, GameEntity> Games { get; } = new();
    public ConcurrentDictionary<string, ThemeEntity> Themes { get; } = new();
    public ConcurrentDictionary<string, HolidayEntity> Holidays { get; } = new();
    public ConcurrentDictionary<string, TableEntity> Activities { get; } = new();
    public ConcurrentDictionary<string, TableEntity> Events { get; } = new();

    public Mock<TableClient> GameTable { get; }
    public Mock<TableClient> ThemeTable { get; }
    public Mock<TableClient> HolidayTable { get; }
    public Mock<TableClient> ActivityTable { get; }
    public Mock<TableClient> EventTable { get; }

    public ListLogger<GameFunctions> GameLog { get; } = new();
    public ListLogger<ThemeFunctions> ThemeLog { get; } = new();
    public ListLogger<HolidayFunctions> HolidayLog { get; } = new();
    public ListLogger<ActivityFunctions> ActivityLog { get; } = new();
    public ListLogger<EventFunctions> EventLog { get; } = new();

    public GameFunctions GameApi { get; }
    public ThemeFunctions ThemeApi { get; }
    public HolidayFunctions HolidayApi { get; }
    public ActivityFunctions ActivityApi { get; }
    public EventFunctions EventApi { get; }

    public LibraryHarness()
    {
        GameTable = TableMocks.Table(Games);
        ThemeTable = TableMocks.Table(Themes);
        HolidayTable = TableMocks.Table(Holidays);
        ActivityTable = TableMocks.Table(Activities);
        EventTable = TableMocks.Table(Events);

        var service = new Mock<TableServiceClient>();
        service.Setup(s => s.GetTableClient("Games")).Returns(GameTable.Object);
        service.Setup(s => s.GetTableClient("Themes")).Returns(ThemeTable.Object);
        service.Setup(s => s.GetTableClient("Holidays")).Returns(HolidayTable.Object);
        service.Setup(s => s.GetTableClient("Activities")).Returns(ActivityTable.Object);
        service.Setup(s => s.GetTableClient("Events")).Returns(EventTable.Object);

        var games = new TableStore<GameEntity>(service.Object, "Games", "Game");
        var activities = new TableStore<ActivityEntity>(service.Object, "Activities", "Activity");
        GameApi = new GameFunctions(games, activities, GameLog);
        ThemeApi = new ThemeFunctions(new TableStore<ThemeEntity>(service.Object, "Themes", "Theme"), activities, ThemeLog);
        HolidayApi = new HolidayFunctions(new TableStore<HolidayEntity>(service.Object, "Holidays", "Holiday"), activities, HolidayLog);
        ActivityApi = new ActivityFunctions(activities, games, ActivityLog);
        EventApi = new EventFunctions(new TableStore<EventEntity>(service.Object, "Events", "Event"), games, activities, new EventGenerator(), EventLog);
    }

    /// <summary>Insert and upsert calls that reached any table. Seeding writes the dictionaries directly, so it never counts.</summary>
    public int Writes() => new[] { GameTable, ThemeTable, HolidayTable, ActivityTable, EventTable }
        .Sum(t => t.Invocations.Count(i => i.Method.Name is nameof(TableClient.UpsertEntityAsync) or nameof(TableClient.AddEntityAsync)));

    /// <summary>Stores a Game directly and returns it, for routes that need one to exist.</summary>
    public GameEntity SeedGame(string name = "Alpha")
    {
        var game = new GameEntity { Id = Guid.NewGuid(), Name = name, PartitionKey = "Game" };
        Games[game.RowKey] = game;
        return game;
    }
}

internal static class ApiResults
{
    // -1 when the result carries no status code, so any status assertion fails loudly.
    public static int Status(IActionResult result) =>
        Assert.IsAssignableFrom<Microsoft.AspNetCore.Mvc.Infrastructure.IStatusCodeActionResult>(result).StatusCode ?? -1;

    public static string Message(IActionResult result) =>
        Assert.IsType<string>(Assert.IsAssignableFrom<ObjectResult>(result).Value);

    public static T Value<T>(IActionResult result) =>
        Assert.IsType<T>(Assert.IsAssignableFrom<ObjectResult>(result).Value);
}
