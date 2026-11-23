using System.Text.Json;
using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;
using MW_GC.EventManager.API.Functions;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// The old-text conversion (#130) against real Table Storage (Azurite). Skipped unless
/// <c>EM_AZURITE_CONNECTION</c> points at local Azurite (<see cref="AzuriteSupport"/>), so <c>dotnet test</c>
/// on the solution needs no emulator and never converts rows in other storage. When <c>EM_API_URL</c> also
/// names a running API on the same Azurite (ending in <c>/api</c>), the converted Comments are read back
/// through its list route too. Each test deletes the rows it seeded (and their Comments) and puts the
/// conversion's marker back as it found it, in <c>finally</c>.
/// </summary>
[TestClass]
public class ActivityNoteConversionAzuriteTests
{
    public TestContext TestContext { get; set; } = null!;

    private sealed record Converted(string Text, DateTimeOffset CreatedUtc, Guid? EventId);

    [TestMethod]
    public async Task OldTextsInRealStorageBecomeOneCommentEachAndASecondRunAddsNone()
    {
        var connection = AzuriteSupport.Connection();
        var apiUrl = AzuriteSupport.ApiUrl();
        var ct = TestContext.CancellationToken;

        var service = new TableServiceClient(connection);
        var activities = new TableStore<ActivityEntity>(service, TableNames.Activities, TableNames.ActivityPartition);
        var comments = new TableStore<CommentEntity>(service, TableNames.Comments, TableNames.CommentPartition);
        var commentApi = new CommentFunctions(comments, activities,
            new TableStore<EventEntity>(service, TableNames.Events, TableNames.EventPartition), new ListLogger<CommentFunctions>());
        var raw = service.GetTableClient(TableNames.Comments);
        var activityTable = service.GetTableClient(TableNames.Activities);
        var conversion = new ActivityNoteConversion(activities, comments, service, new ListLogger<ActivityNoteConversion>());

        // Old-style data, written straight to the Activities table as raw rows: the Activity no longer has the
        // field (#133), so the old single text is in the old-text column only. Two rows carry text, one carries
        // none (no column), and one carries a number in that column (skipped, never thrown on). The first
        // has a fixed id, so its Comment's id can be compared with a literal.
        var gameId = Guid.NewGuid();
        var first = OldActivityRows.FixedActivityId;
        var second = Guid.NewGuid();
        var none = Guid.NewGuid();
        var number = Guid.NewGuid();
        var seededIds = new[] { first, second, none, number };
        await CleanUpAsync(activityTable, comments, seededIds, ct);
        var markerFound = await AzuriteSupport.ReadMarkerAsync(raw, ct);
        try
        {
            await activityTable.UpsertEntityAsync(OldActivityRows.Row(first, gameId, "Old text one", "  Ran long, cut the second round  "), TableUpdateMode.Replace, ct);
            await activityTable.UpsertEntityAsync(OldActivityRows.Row(second, gameId, "Old text two", "Bring snacks"), TableUpdateMode.Replace, ct);
            await activityTable.UpsertEntityAsync(OldActivityRows.Row(none, gameId, "No old text", null), TableUpdateMode.Replace, ct);
            await activityTable.UpsertEntityAsync(OldActivityRows.Row(number, gameId, "A number in the old column", 42), TableUpdateMode.Replace, ct);
            var seeded = new Dictionary<Guid, TableEntity>();
            foreach (var id in seededIds)
                seeded[id] = (await activityTable.GetEntityAsync<TableEntity>(TableNames.ActivityPartition, id.ToString("D"), cancellationToken: ct)).Value;
            TestContext.Flow("seeded rows (old column of: text, text, none, number)", "String, String, none, Int32",
                string.Join(", ", seededIds.Select(id => seeded[id].TryGetValue(OldActivityRows.OldTextColumn, out var v) ? v.GetType().Name : "none")));

            if (apiUrl is not null)
            {
                var before = 0;
                foreach (var id in seededIds) before += (await ListThroughApiAsync(apiUrl, id, ct)).Count;
                TestContext.Flow("list route of a running API before the conversion", 0, before);
            }

            // The host-start pass over old data: any marker (left by the running host or an earlier run) is
            // deleted first, so only this pass can store it.
            await raw.DeleteEntityAsync(ActivityNoteConversion.MarkerPartitionKey, ActivityNoteConversion.MarkerRowKey, cancellationToken: ct);
            TestContext.Flow("marker before the host-start pass", false, await MarkerStoredAsync(raw, ct));
            var startedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            var startupLog = new ListLogger<ActivityNoteConversionStartup>();
            await using (var host = new ServiceCollection().AddSingleton(conversion).BuildServiceProvider())
            {
                var startup = new ActivityNoteConversionStartup(host, startupLog);
                await startup.StartAsync(ct);
                await startup.ExecuteTask!;
            }
            TestContext.Flow("host-start pass logged no failure", 0, startupLog.Entries.Count);
            var marker = await raw.GetEntityIfExistsAsync<TableEntity>(
                ActivityNoteConversion.MarkerPartitionKey, ActivityNoteConversion.MarkerRowKey, cancellationToken: ct);
            TestContext.Flow("host-start pass stored a new marker", true, marker.HasValue && marker.Value!.GetDateTimeOffset("CompletedUtc") >= startedAt);

            // The trimmed texts each Comment must carry, written out rather than worked out.
            var expectedText = new Dictionary<Guid, string> { [first] = "Ran long, cut the second round", [second] = "Bring snacks" };
            var stored = await CommentsOfAsync(comments, seededIds, ct);
            TestContext.Flow("Comments for the seeded Activities after the pass", 2, stored.Count);
            foreach (var (id, text) in expectedText)
            {
                var comment = stored.Single(c => c.ActivityId == id);
                TestContext.Flow($"Comment of {id}", new Converted(text, seeded[id].Timestamp!.Value, null),
                    new Converted(comment.Text, comment.CreatedUtc, comment.EventId));
            }
            TestContext.Flow("id of the fixed Activity's Comment", OldActivityRows.FixedActivityCommentId, stored.Single(c => c.ActivityId == first).Id);
            TestContext.Flow("rows with nothing to convert have no Comment", 0, stored.Count(c => c.ActivityId == none || c.ActivityId == number));

            // The Comment list filter matches only a Guid-typed ActivityId (#160): the stored column is one.
            var rawComment = (await raw.GetEntityAsync<TableEntity>(
                TableNames.CommentPartition, OldActivityRows.FixedActivityCommentId.ToString("D"), cancellationToken: ct)).Value;
            TestContext.Flow("stored ActivityId column type", nameof(Guid), rawComment["ActivityId"]?.GetType().Name);
            var listed = ApiResults.Value<List<CommentEntity>>(await commentApi.List(TestRequests.Empty(), first, ct));
            TestContext.Flow("list route over real storage finds the converted Comment", OldActivityRows.FixedActivityCommentId, Assert.ContainsSingle(listed).Id);

            var after = (await activityTable.GetEntityAsync<TableEntity>(TableNames.ActivityPartition, first.ToString("D"), cancellationToken: ct)).Value;
            TestContext.Flow("old column untouched", ("  Ran long, cut the second round  ", seeded[first].ETag),
                (after.GetString(OldActivityRows.OldTextColumn), after.ETag));

            if (apiUrl is not null)
            {
                foreach (var (id, text) in expectedText)
                {
                    var throughApi = await ListThroughApiAsync(apiUrl, id, ct);
                    TestContext.Flow($"list route GET {apiUrl}/activities/{id}/comments",
                        $"1: {new Converted(text, seeded[id].Timestamp!.Value, null)}",
                        $"{throughApi.Count}: {string.Join("; ", throughApi)}");
                }
                foreach (var id in new[] { none, number })
                    TestContext.Flow($"list route, row {id} with nothing to convert", 0, (await ListThroughApiAsync(apiUrl, id, ct)).Count);
            }

            // Second run: the marker is there, so nothing is added.
            await conversion.RunAsync(ct);
            TestContext.Flow("second run", 2, (await CommentsOfAsync(comments, seededIds, ct)).Count);

            // Third run with the marker removed again: the deterministic ids still add no duplicate in real storage.
            await raw.DeleteEntityAsync(ActivityNoteConversion.MarkerPartitionKey, ActivityNoteConversion.MarkerRowKey, cancellationToken: ct);
            await conversion.RunAsync(ct);
            TestContext.Flow("rerun without the marker (inserts refused as EntityAlreadyExists)", 2, (await CommentsOfAsync(comments, seededIds, ct)).Count);
            TestContext.Flow("marker after the rerun", true, await MarkerStoredAsync(raw, ct));

            if (apiUrl is not null)
            {
                var total = 0;
                foreach (var id in seededIds) total += (await ListThroughApiAsync(apiUrl, id, ct)).Count;
                TestContext.Flow("list route of a running API after the reruns", 2, total);
            }
        }
        finally
        {
            await CleanUpAsync(activityTable, comments, seededIds, ct);
            await AzuriteSupport.RestoreMarkerAsync(raw, markerFound, ct);
        }
    }

    [TestMethod]
    public async Task APutBeforeAnyPassKeepsTheOldTextAsACommentInRealStorage()
    {
        var connection = AzuriteSupport.Connection();
        var ct = TestContext.CancellationToken;

        // A fresh conversion and Activity routes over real storage, as a host that has not run its startup pass.
        var service = new TableServiceClient(connection);
        var games = new TableStore<GameEntity>(service, TableNames.Games, TableNames.GamePartition);
        var activities = new TableStore<ActivityEntity>(service, TableNames.Activities, TableNames.ActivityPartition);
        var comments = new TableStore<CommentEntity>(service, TableNames.Comments, TableNames.CommentPartition);
        var conversion = new ActivityNoteConversion(activities, comments, service, new ListLogger<ActivityNoteConversion>());
        var activityApi = new ActivityFunctions(activities, games, comments, conversion, new ListLogger<ActivityFunctions>());
        var commentApi = new CommentFunctions(comments, activities,
            new TableStore<EventEntity>(service, TableNames.Events, TableNames.EventPartition), new ListLogger<CommentFunctions>());
        var activityTable = service.GetTableClient(TableNames.Activities);
        var raw = service.GetTableClient(TableNames.Comments);

        var game = new GameEntity { Id = Guid.NewGuid(), Name = "Azurite game" };
        var activityId = Guid.NewGuid();
        var markerFound = await AzuriteSupport.ReadMarkerAsync(raw, ct);
        try
        {
            await games.UpsertAsync(game, ct);
            await activityTable.AddEntityAsync(OldActivityRows.Row(activityId, game.Id, "Old activity", "  Old text  "), ct);
            var seeded = (await activityTable.GetEntityAsync<TableEntity>(TableNames.ActivityPartition, activityId.ToString("D"), cancellationToken: ct)).Value;
            await raw.DeleteEntityAsync(ActivityNoteConversion.MarkerPartitionKey, ActivityNoteConversion.MarkerRowKey, cancellationToken: ct);
            TestContext.Flow($"marker before the PUT (Activity {activityId} holds \"{seeded.GetString(OldActivityRows.OldTextColumn)}\")",
                false, await MarkerStoredAsync(raw, ct));

            var put = await activityApi.Update(TestRequests.Json(new { gameId = game.Id, name = "Renamed" }), activityId, ct);
            var stored = (await activityTable.GetEntityAsync<TableEntity>(TableNames.ActivityPartition, activityId.ToString("D"), cancellationToken: ct)).Value;
            TestContext.Flow("PUT replaced the row (status, name, old column present)", (200, "Renamed", false),
                (ApiResults.Status(put), stored.GetString("Name"), stored.ContainsKey(OldActivityRows.OldTextColumn)));

            var listed = ApiResults.Value<List<CommentEntity>>(await commentApi.List(TestRequests.Empty(), activityId, ct));
            var kept = Assert.ContainsSingle(listed);
            TestContext.Flow("old text kept as a Comment", new Converted("Old text", seeded.Timestamp!.Value, null),
                new Converted(kept.Text, kept.CreatedUtc, kept.EventId));

            var again = await activityApi.Update(TestRequests.Json(new { gameId = game.Id, name = "Renamed again" }), activityId, ct);
            var afterSecond = ApiResults.Value<List<CommentEntity>>(await commentApi.List(TestRequests.Empty(), activityId, ct));
            TestContext.Flow("second PUT (status, Comments, same Comment)", (200, 1, true),
                (ApiResults.Status(again), afterSecond.Count, afterSecond.Count == 1 && afterSecond[0].Id == kept.Id));
            TestContext.Flow("marker after the PUT", true, await MarkerStoredAsync(raw, ct));
        }
        finally
        {
            await CleanUpAsync(activityTable, comments, [activityId], ct);
            await games.DeleteAsync(game.Id, ct: ct);
            await AzuriteSupport.RestoreMarkerAsync(raw, markerFound, ct);
        }
    }

    private static async Task<bool> MarkerStoredAsync(TableClient raw, CancellationToken ct) =>
        (await raw.GetEntityIfExistsAsync<TableEntity>(
            ActivityNoteConversion.MarkerPartitionKey, ActivityNoteConversion.MarkerRowKey, cancellationToken: ct)).HasValue;

    // Deletes the seeded Activity rows and every Comment of theirs (the converted one by its derived id
    // too), so a later run starts clean.
    private static async Task CleanUpAsync(TableClient activityTable, TableStore<CommentEntity> comments, IEnumerable<Guid> activityIds, CancellationToken ct)
    {
        foreach (var id in activityIds)
        {
            foreach (var comment in await comments.GetWhereAsync(c => c.ActivityId == id, ct))
                await comments.DeleteAsync(comment.Id, ct: ct);
            await comments.DeleteAsync(ActivityNoteConversion.CommentIdFor(id), ct: ct);
            await activityTable.DeleteEntityAsync(TableNames.ActivityPartition, id.ToString("D"), cancellationToken: ct);
        }
    }

    private static async Task<List<CommentEntity>> CommentsOfAsync(
        TableStore<CommentEntity> comments, IEnumerable<Guid> activityIds, CancellationToken ct)
    {
        var ids = activityIds.ToHashSet();
        return (await comments.GetAllAsync(ct)).Where(c => ids.Contains(c.ActivityId)).ToList();
    }

    private static async Task<List<Converted>> ListThroughApiAsync(string apiUrl, Guid activityId, CancellationToken ct)
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync($"{apiUrl}/activities/{activityId}/comments", ct);
        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode, $"GET comments of {activityId}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.EnumerateArray().Select(c => new Converted(
            c.GetProperty("text").GetString()!,
            c.GetProperty("createdUtc").GetDateTimeOffset(),
            c.GetProperty("eventId").ValueKind == JsonValueKind.Null ? null : c.GetProperty("eventId").GetGuid())).ToList();
    }
}
