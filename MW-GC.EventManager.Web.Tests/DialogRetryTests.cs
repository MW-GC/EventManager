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
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;
using MW_GC.EventManager.Web.Pages;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Tests;

// The Events page's customize dialog against the real API handlers in process (moved from
// IdempotentCreateTests): a lost create response is retried with the same Idempotency-Key.
// Ported with its reflection style; issue #58 makes the planner properly testable later.
[TestClass]
public class DialogRetryTests
{
    private readonly ConcurrentDictionary<string, TableEntity> rows = new();
    private readonly EventFunctions api;
    private readonly EventEntity input;

    public DialogRetryTests()
    {
        var game = new Game { Name = "Game", Id = Guid.NewGuid() };
        input = new EventEntity
        {
            Name = "Retry me", Date = DateTimeOffset.Parse("2026-09-21T19:00:00Z"),
            Selections = [new Selection { Game = game, Activity = new Activity { Id = Guid.NewGuid(), GameId = game.Id, Name = "Activity" } }]
        };
        var table = new Mock<TableClient>();
        table.Setup(t => t.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>()))
            .Returns((TableEntity row, CancellationToken _) => rows.TryAdd(row.RowKey, new TableEntity(row))
                ? Task.FromResult(Mock.Of<Response>())
                : Task.FromException<Response>(new RequestFailedException(409, "Entity already exists", "EntityAlreadyExists", null)));
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
        api = new(new(service.Object, "events", "events"), new(service.Object, "games", "games"), new(service.Object, "activities", "activities"), Microsoft.Extensions.Logging.Abstractions.NullLogger<EventFunctions>.Instance);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    [DataRow(true, false, true)]
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
            // ShowCustomize randomizes three slots and keeps none when the pool is smaller;
            // pin the single-slot state this test saves instead of relying on that draw.
            var slotType = typeof(Events).GetNestedType("SlotSelection", BindingFlags.NonPublic)!;
            var slot = Activator.CreateInstance(slotType)!;
            slotType.GetProperty("GameId")!.SetValue(slot, selection.Game.Id);
            slotType.GetProperty("ActivityId")!.SetValue(slot, selection.Activity.Id);
            var slots = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(slotType))!;
            slots.Add(slot);
            Set("_custSelections", slots);
            Set("_custName", input.Name);
        }
        var handler = new ApiHandler(api) { Failures = 2, CommitBeforeFailure = committed, Timeout = timeout };
        typeof(Events).GetProperty("EventSvc", flags)!.SetValue(page,
            new EventService(new HttpClient(handler) { BaseAddress = new Uri("https://example.test") }));
        NewDialog();
        await Save();
        await Save();
        Assert.AreEqual(true, Get("_showCustomize"));
        Assert.IsNotNull(Get("_customizeError"));
        Assert.AreEqual(committed ? 1 : 0, (await Listed()).Count);
        if (changeDetails)
        {
            Set("_custName", "Changed after ambiguous save");
            await Save();
            Assert.AreEqual(true, Get("_showCustomize"));
            Assert.AreEqual("Changed after ambiguous save", Get("_custName"));
            Assert.Contains("different details", (string)Get("_customizeError")!);
            Assert.AreEqual(input.Name, Assert.ContainsSingle(await Listed()).Name);
            Set("_custName", input.Name);
        }
        await Save();
        Assert.AreEqual(false, Get("_showCustomize"));
        Assert.IsNull(Get("_customizeError"));
        var saved = Assert.ContainsSingle(await Listed());
        Assert.AreEqual(selection.Activity.Id, saved.WinnerActivityId);
        foreach (var key in handler.Keys) Assert.AreEqual(saved.Id.ToString("D"), key);
        Assert.AreEqual(saved.Id, Assert.ContainsSingle((List<EventEntity>)Get("_events")!).Id);

        typeof(Events).GetMethod("ShowEditEvent", flags)!.Invoke(page, [saved]);
        Set("_custName", "Edited");
        await Save();
        Assert.AreEqual(HttpMethod.Put, handler.LastMethod);
        Assert.IsNull(handler.LastKey);
        Assert.AreEqual("Edited", Assert.ContainsSingle(await Listed()).Name);
        NewDialog();
        await Save();
        Assert.AreEqual(2, (await Listed()).Count);
        Assert.AreNotEqual(saved.Id.ToString("D"), handler.LastKey);
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

    private async Task<List<EventEntity>> Listed() => Assert.IsExactInstanceOfType<List<EventEntity>>(
        Assert.IsExactInstanceOfType<OkObjectResult>(await api.GetAll(Request(input), default)).Value);
}
