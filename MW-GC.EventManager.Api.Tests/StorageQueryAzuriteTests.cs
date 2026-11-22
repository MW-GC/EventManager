using Azure.Data.Tables;
using Microsoft.AspNetCore.Mvc;
using MW_GC.EventManager.API.Functions;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// Queries the in-memory tables can only imitate, sent to real Table Storage (Azurite): the Comment
/// list and the Activity-delete cascade (#160), and the bulk Like count (#177). Skipped unless
/// <c>EM_AZURITE_CONNECTION</c> is set (<see cref="AzuriteSupport"/>). Each test deletes what it seeded.
/// </summary>
[TestClass]
public class StorageQueryAzuriteTests
{
    public TestContext TestContext { get; set; } = null!;

    private void Flow<T>(string name, T expected, T actual)
    {
        var pass = EqualityComparer<T>.Default.Equals(expected, actual);
        TestContext.WriteLine($"{(pass ? "PASS" : "FAIL")} {name}: expected {expected}, actual {actual}");
        Assert.AreEqual(expected, actual, name);
    }

    [TestMethod]
    public async Task CommentListAndActivityDeleteQueryRealStorageForThatActivityOnly()
    {
        var connection = AzuriteSupport.Connection();
        var ct = TestContext.CancellationToken;
        var service = new TableServiceClient(connection);
        var games = new TableStore<GameEntity>(service, "Games", "Game");
        var activities = new TableStore<ActivityEntity>(service, TableNames.Activities, TableNames.ActivityPartition);
        var comments = new TableStore<CommentEntity>(service, TableNames.Comments, TableNames.CommentPartition);
        var conversion = new ActivityNoteConversion(activities, comments, service, new ListLogger<ActivityNoteConversion>());
        var activityApi = new ActivityFunctions(activities, games, comments, conversion, new ListLogger<ActivityFunctions>());
        var commentApi = new CommentFunctions(comments, activities,
            new TableStore<EventEntity>(service, "Events", "Event"), new ListLogger<CommentFunctions>());
        var raw = service.GetTableClient(TableNames.Comments);

        var game = new GameEntity { Id = Guid.NewGuid(), Name = "Azurite query game" };
        var created = new List<Guid>();
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

            var a = await CreateActivity("Query A");
            var b = await CreateActivity("Query B");
            await AddComment(a, "A one");
            await AddComment(a, "A two");
            var bComment = await AddComment(b, "B one");
            await raw.AddEntityAsync(new TableEntity(foreignPartition, foreignRow) { ["ActivityId"] = a, ["Text"] = "Not a Comment" }, ct);

            Flow("list of A over real storage", "A one, A two", string.Join(", ", await ListTexts(a)));
            Flow("list of B over real storage", "B one", string.Join(", ", await ListTexts(b)));

            var deleted = await activityApi.Delete(TestRequests.Empty(), a, ct);
            Flow("Activity delete of A", nameof(NoContentResult), deleted.GetType().Name);
            Flow("A's Comments left in storage", 0, (await comments.GetWhereAsync(c => c.ActivityId == a, ct)).Count);
            Flow("B's Comment kept", true, await comments.GetAsync(bComment, ct) is not null);
            Flow("row of another partition with A's id kept", true,
                (await raw.GetEntityIfExistsAsync<TableEntity>(foreignPartition, foreignRow, cancellationToken: ct)).HasValue);
            Flow("list of B after A's delete", "B one", string.Join(", ", await ListTexts(b)));
        }
        finally
        {
            foreach (var id in created)
                await activityApi.Delete(TestRequests.Empty(), id, ct);
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

            Flow($"Like count of {eventId}", 2, counts.GetValueOrDefault(eventId));
        }
        finally
        {
            foreach (var row in rows)
                await likes.DeleteEntityAsync(row.PartitionKey, row.RowKey, cancellationToken: ct);
        }
    }
}
