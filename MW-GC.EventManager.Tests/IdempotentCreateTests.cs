using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
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
using MW_GC.EventManager.Web.Pages;
using MW_GC.EventManager.Web.Services;
using Xunit;

namespace MW_GC.EventManager.Tests;

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
        api = new(new(service.Object, "events", "events"), new(service.Object, "games", "games"), new(service.Object, "activities", "activities"), new());
    }

    [Fact]
    public async Task LostCreateResponseThenRepeatedRetryReturnsOnePersistedEvent()
    {
        var key = Guid.NewGuid().ToString("D");
        await api.SaveCustomized(Request(input, key), default); // Committed response never reaches client.
        for (var retry = 0; retry < 3; retry++)
            Assert.IsType<OkObjectResult>(await api.SaveCustomized(Request(input, key), default));
        Assert.Equal(Guid.Parse(key), Assert.Single(await Listed()).Id);
    }

    [Fact]
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
        Assert.Single(results.OfType<CreatedResult>());
        Assert.Single(results.OfType<OkObjectResult>());
        Assert.Equal(Guid.Parse(key), Assert.Single(await Listed()).Id);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("date")]
    [InlineData("snapshot")]
    [InlineData("unique")]
    public async Task ChangedRetryConflictsInsteadOfDiscardingDetails(string change)
    {
        var key = Guid.NewGuid().ToString("D");
        await api.SaveCustomized(Request(input, key), default);
        var before = JsonSerializer.Serialize(Assert.Single(await Listed()));
        switch (change)
        {
            case "name": input.Name = "Changed"; break;
            case "date": input.Date = input.Date.AddHours(1); break;
            case "snapshot": input.Selections[0] = input.Selections[0] with { Activity = input.Selections[0].Activity with { Comments = "Changed" } }; break;
            case "unique": input.UniqueGamesOnly = false; break;
        }
        Assert.IsType<ConflictObjectResult>(await api.SaveCustomized(Request(input, key), default));
        Assert.Equal(before, JsonSerializer.Serialize(Assert.Single(await Listed())));
    }

    [Fact]
    public async Task RetryIgnoresStorageMetadataButNeverOverwritesLaterUpdate()
    {
        var key = Guid.NewGuid().ToString("D");
        await api.SaveCustomized(Request(input, key), default);
        rows[key].Timestamp = DateTimeOffset.UtcNow;
        rows[key].ETag = new ETag("storage-version");
        input.Date = input.Date.ToOffset(TimeSpan.FromHours(3));
        Assert.IsType<OkObjectResult>(await api.SaveCustomized(Request(input, key), default));
        var saved = Assert.Single(await Listed());
        saved.Name = "Later edit";
        Assert.IsType<OkObjectResult>(await api.Update(Request(saved), saved.Id, default));
        Assert.IsType<ConflictObjectResult>(await api.SaveCustomized(Request(input, key), default));
        Assert.Equal("Later edit", Assert.Single(await Listed()).Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad-key")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("11111111111111111111111111111111")]
    public async Task InvalidKeyDoesNotCreate(string key)
    {
        Assert.IsType<BadRequestObjectResult>(await api.SaveCustomized(Request(input, key), default));
        Assert.Empty(await Listed());
    }

    [Fact]
    public async Task LegacyClientsStillCreateIndependentEvents()
    {
        await api.SaveCustomized(Request(input), default);
        await api.SaveCustomized(Request(input), default);
        Assert.Equal(2, (await Listed()).Select(e => e.Id).Distinct().Count());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(true, false, true)]
    public async Task DialogRetriesSameCreateAfterTransportFailure(bool committed, bool timeout, bool changeDetails = false)
    {
        var page = new Events();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Set(string name, object value) => typeof(Events).GetField(name, flags)!.SetValue(page, value);
        object? Get(string name) => typeof(Events).GetField(name, flags)!.GetValue(page);
        Task Save() => (Task)typeof(Events).GetMethod("SaveCustomizedEvent", flags)!.Invoke(page, null)!;
        var selection = input.Selections[0];
        Set("_games", new List<GameEntity> { new() { Id = selection.Game.Id, Name = selection.Game.Name } });
        Set("_activities", new List<ActivityEntity> { new() { Id = selection.Activity.Id, GameId = selection.Game.Id, Name = selection.Activity.Name } });
        void NewDialog()
        {
            typeof(Events).GetMethod("ShowCustomize", flags)!.Invoke(page, null);
            Set("_custName", input.Name);
        }
        var handler = new ApiHandler(api) { Failures = 2, CommitBeforeFailure = committed, Timeout = timeout };
        typeof(Events).GetProperty("EventSvc", flags)!.SetValue(page,
            new EventService(new HttpClient(handler) { BaseAddress = new Uri("https://example.test") }));
        NewDialog();
        await Save();
        await Save();
        Assert.Equal(true, Get("_showCustomize"));
        Assert.NotNull(Get("_customizeError"));
        Assert.Equal(committed ? 1 : 0, (await Listed()).Count);
        if (changeDetails)
        {
            Set("_custName", "Changed after ambiguous save");
            await Save();
            Assert.Equal(true, Get("_showCustomize"));
            Assert.Equal("Changed after ambiguous save", Get("_custName"));
            Assert.Contains("different details", (string)Get("_customizeError")!);
            Assert.Equal(input.Name, Assert.Single(await Listed()).Name);
            Set("_custName", input.Name);
        }
        await Save();
        Assert.Equal(false, Get("_showCustomize"));
        Assert.Null(Get("_customizeError"));
        var saved = Assert.Single(await Listed());
        Assert.Equal(selection.Activity.Id, saved.WinnerActivityId);
        Assert.All(handler.Keys, key => Assert.Equal(saved.Id.ToString("D"), key));
        Assert.Equal(saved.Id, Assert.Single((List<EventEntity>)Get("_events")!).Id);

        typeof(Events).GetMethod("ShowEditEvent", flags)!.Invoke(page, [saved]);
        Set("_custName", "Edited");
        await Save();
        Assert.Equal(HttpMethod.Put, handler.LastMethod);
        Assert.Null(handler.LastKey);
        Assert.Equal("Edited", Assert.Single(await Listed()).Name);
        NewDialog();
        await Save();
        Assert.Equal(2, (await Listed()).Count);
        Assert.NotEqual(saved.Id.ToString("D"), handler.LastKey);
    }

    private sealed class ApiHandler(EventFunctions api) : HttpMessageHandler
    {
        public int Failures { get; set; }
        public bool CommitBeforeFailure { get; init; }
        public bool Timeout { get; init; }
        public List<string?> Keys { get; } = [];
        public string? LastKey { get; private set; }
        public HttpMethod? LastMethod { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                var all = (OkObjectResult)await api.GetAll(new DefaultHttpContext().Request, cancellationToken);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(all.Value) };
            }
            LastMethod = request.Method;
            LastKey = request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null;
            if (request.Method == HttpMethod.Post) Keys.Add(LastKey);
            var fail = Failures-- > 0;
            void LoseResponse()
            {
                if (Timeout) throw new TaskCanceledException("Timed out after commit");
                throw new HttpRequestException("Connection lost");
            }
            if (fail && !CommitBeforeFailure) LoseResponse();
            var entity = (await request.Content!.ReadFromJsonAsync<EventEntity>(cancellationToken))!;
            var result = (ObjectResult)(request.Method == HttpMethod.Post
                ? await api.SaveCustomized(Request(entity, LastKey), cancellationToken)
                : await api.Update(Request(entity), entity.Id, cancellationToken));
            if (fail) LoseResponse();
            return new((HttpStatusCode)result.StatusCode!) { Content = JsonContent.Create(result.Value) };
        }
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

    private async Task<List<EventEntity>> Listed() => Assert.IsType<List<EventEntity>>(
        Assert.IsType<OkObjectResult>(await api.GetAll(Request(input), default)).Value);
}
