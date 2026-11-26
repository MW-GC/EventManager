using System.Text.Json;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Mvc;
using MW_GC.EventManager.API.Functions;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// Queries the in-memory tables can only imitate, sent to real Table Storage (Azurite): the Comment
/// list and the Activity-delete cascade (#160), and the bulk Like count (#177). Skipped unless
/// <c>EM_AZURITE_CONNECTION</c> points at local Azurite (<see cref="AzuriteSupport"/>). Each test deletes what it seeded.
/// </summary>
[TestClass]
public class StorageQueryAzuriteTests
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Counts the rows storage answers each query of one table with, from the HTTP responses themselves,
    /// so a filter applied after the read (in memory) cannot hide a whole-partition read.
    /// </summary>
    private sealed class RowsReturned(string table) : HttpPipelinePolicy
    {
        public List<int> Queries { get; } = [];

        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            ProcessNext(message, pipeline);
            Count(message);
        }

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            await ProcessNextAsync(message, pipeline);
            Count(message);
        }

        // A query of the table is a GET of "<table>()"; its rows are the "value" array of the JSON answer.
        private void Count(HttpMessage message)
        {
            if (message.Request.Method != RequestMethod.Get
                || !message.Request.Uri.Path.EndsWith($"/{table}()", StringComparison.Ordinal)
                || message.Response.ContentStream is not { } body)
                return;
            var copy = new MemoryStream();
            body.CopyTo(copy);
            copy.Position = 0;
            message.Response.ContentStream = copy;
            using var json = JsonDocument.Parse(copy.ToArray());
            Queries.Add(json.RootElement.GetProperty("value").GetArrayLength());
        }
    }

    [TestMethod]
    public async Task CommentListAndActivityDeleteQueryRealStorageForThatActivityOnly()
    {
        var connection = AzuriteSupport.Connection();
        var ct = TestContext.CancellationToken;
        var rowsReturned = new RowsReturned(TableNames.Comments);
        var options = new TableClientOptions();
        options.AddPolicy(rowsReturned, HttpPipelinePosition.PerCall);
        var service = new TableServiceClient(connection, options);
        var games = new TableStore<GameEntity>(service, TableNames.Games, TableNames.GamePartition);
        var activities = new TableStore<ActivityEntity>(service, TableNames.Activities, TableNames.ActivityPartition);
        var comments = new TableStore<CommentEntity>(service, TableNames.Comments, TableNames.CommentPartition);
        var activityApi = new ActivityFunctions(activities, games, comments, new ListLogger<ActivityFunctions>());
        var commentApi = new CommentFunctions(comments, activities,
            new TableStore<EventEntity>(service, TableNames.Events, TableNames.EventPartition), new ListLogger<CommentFunctions>());
        // The raw client checks storage without the code under test, and seeds rows the routes cannot write.
        var raw = new TableServiceClient(connection).GetTableClient(TableNames.Comments);

        var game = new GameEntity { Id = Guid.NewGuid(), Name = "Azurite query game" };
        var created = new List<Guid>();
        // Comments of other Activities in the same partition: a list that read the whole partition and
        // filtered in memory would be answered with these too.
        var others = Enumerable.Range(0, 5).Select(i => new TableEntity(TableNames.CommentPartition, Guid.NewGuid().ToString("D"))
        {
            ["ActivityId"] = Guid.NewGuid(), ["Text"] = $"Another Activity's Comment {i}", ["CreatedUtc"] = DateTimeOffset.UtcNow,
        }).ToList();
        // A row of another partition of the Comment table that carries A's id: only the partition
        // condition of the query keeps it out.
        var foreignPartition = "Other-" + Guid.NewGuid().ToString("N");
        var foreignRow = Guid.NewGuid().ToString("D");
        try
        {
            await games.UpsertAsync(game, ct);
            async Task<Guid> CreateActivity(string name)
            {
                var result = await activityApi.Create(TestRequests.Json(new { name, gameId = game.Id }), ct);
                var id = ApiResults.Value<ActivityEntity>(result).Id;
                created.Add(id);
                return id;
            }
            async Task<Guid> AddComment(Guid activityId, string text) =>
                ApiResults.Value<CommentEntity>(await commentApi.Add(TestRequests.Json(new { text }), activityId, ct)).Id;
            async Task<List<string>> ListTexts(Guid activityId) =>
                ApiResults.Value<List<CommentEntity>>(await commentApi.List(TestRequests.Empty(), activityId, ct))
                    .Select(c => c.Text).Order().ToList();
            async Task<bool> StoredRaw(string partition, string row) =>
                (await raw.GetEntityIfExistsAsync<TableEntity>(partition, row, cancellationToken: ct)).HasValue;
            async Task<int> CountStoredRaw(IEnumerable<(string Partition, string Row)> keys)
            {
                var stored = 0;
                foreach (var (partition, row) in keys)
                    if (await StoredRaw(partition, row)) stored++;
                return stored;
            }

            var a = await CreateActivity("Query A");
            var b = await CreateActivity("Query B");
            var aComments = new[] { await AddComment(a, "A one"), await AddComment(a, "A two") };
            var bComment = await AddComment(b, "B one");
            foreach (var other in others) await raw.AddEntityAsync(other, ct);
            await raw.AddEntityAsync(new TableEntity(foreignPartition, foreignRow) { ["ActivityId"] = a, ["Text"] = "Not a Comment" }, ct);
            rowsReturned.Queries.Clear();

            TestContext.Flow("list of A over real storage", "A one, A two", string.Join(", ", await ListTexts(a)));
            TestContext.Flow("list of B over real storage", "B one", string.Join(", ", await ListTexts(b)));
            TestContext.Flow("rows storage returned to the two list queries", "2, 1", string.Join(", ", rowsReturned.Queries));

            rowsReturned.Queries.Clear();
            var deleted = await activityApi.Delete(TestRequests.Empty(), a, ct);
            TestContext.Flow("Activity delete of A", nameof(NoContentResult), deleted.GetType().Name);
            TestContext.Flow("rows storage returned to the delete's Comment query", "2", string.Join(", ", rowsReturned.Queries));
            TestContext.Flow("A's Comments left in storage (raw reads)", 0,
                await CountStoredRaw(aComments.Select(id => (TableNames.CommentPartition, id.ToString("D")))));
            TestContext.Flow("B's Comment kept (raw read)", true, await StoredRaw(TableNames.CommentPartition, bComment.ToString("D")));
            TestContext.Flow("other Activities' Comments kept (raw reads)", others.Count,
                await CountStoredRaw(others.Select(o => (o.PartitionKey, o.RowKey))));
            TestContext.Flow("row of another partition with A's id kept (raw read)", true, await StoredRaw(foreignPartition, foreignRow));
            TestContext.Flow("list of B after A's delete", "B one", string.Join(", ", await ListTexts(b)));
        }
        finally
        {
            foreach (var id in created)
                await activityApi.Delete(TestRequests.Empty(), id, ct);
            foreach (var other in others)
                await raw.DeleteEntityAsync(other.PartitionKey, other.RowKey, cancellationToken: ct);
            await raw.DeleteEntityAsync(foreignPartition, foreignRow, cancellationToken: ct);
            await games.DeleteAsync(game.Id, ct: ct);
        }
    }

    [TestMethod]
    public async Task LikeCountInRealStorageCountsOnlyDFormEventIds()
    {
        var connection = AzuriteSupport.Connection();
        var ct = TestContext.CancellationToken;
        var service = new TableServiceClient(connection);
        var feedback = new FeedbackStore(service);
        var likes = service.GetTableClient(FeedbackStore.LikesTable);

        var eventId = Guid.NewGuid();
        // Two Likes under the Event id as the API writes it ("D"), one under the same id in braces and
        // one without hyphens: only the first two are this Event's.
        var rows = new[]
        {
            new TableEntity(eventId.ToString("D"), Guid.NewGuid().ToString("N")),
            new TableEntity(eventId.ToString("D"), Guid.NewGuid().ToString("N")),
            new TableEntity(eventId.ToString("B"), Guid.NewGuid().ToString("N")),
            new TableEntity(eventId.ToString("N"), Guid.NewGuid().ToString("N")),
        };
        try
        {
            foreach (var row in rows) await likes.AddEntityAsync(row, ct);

            var counts = await feedback.CountLikesByEventAsync(ct);

            TestContext.Flow($"Like count of {eventId}", 2, counts.GetValueOrDefault(eventId));
        }
        finally
        {
            foreach (var row in rows)
                await likes.DeleteEntityAsync(row.PartitionKey, row.RowKey, cancellationToken: ct);
        }
    }
}
