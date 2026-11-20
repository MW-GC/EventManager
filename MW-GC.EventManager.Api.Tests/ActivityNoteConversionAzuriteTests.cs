using System.Text.Json;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// The old-note conversion (#130) against real Table Storage (Azurite). Skipped unless
/// <c>EM_AZURITE_CONNECTION</c> names the Azurite connection string, so <c>dotnet test</c> on the
/// solution needs no emulator. When <c>EM_API_URL</c> also names a running API on the same Azurite
/// (ending in <c>/api</c>), the converted Comments are read back through its list route too.
/// </summary>
[TestClass]
public class ActivityNoteConversionAzuriteTests
{
    public const string ConnectionVariable = "EM_AZURITE_CONNECTION";
    public const string ApiUrlVariable = "EM_API_URL";

    public TestContext TestContext { get; set; } = null!;

    private void Flow(bool pass, string name, string detail)
    {
        TestContext.WriteLine($"{(pass ? "PASS" : "FAIL")} {name}: {detail}");
        Assert.IsTrue(pass, $"{name}: {detail}");
    }

    [TestMethod]
    public async Task OldNotesInRealStorageBecomeOneCommentEachAndASecondRunAddsNone()
    {
        var connection = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(connection))
            Assert.Inconclusive($"Azurite test skipped: set {ConnectionVariable} to an Azurite connection string.");
        var apiUrl = Environment.GetEnvironmentVariable(ApiUrlVariable)?.Trim().TrimEnd('/');
        var ct = TestContext.CancellationToken;

        var service = new TableServiceClient(connection);
        var activities = new TableStore<ActivityEntity>(service, "Activities", "Activity");
        var comments = new TableStore<CommentEntity>(service, "ActivityComments", "Comment");
        var raw = service.GetTableClient("ActivityComments");
        var conversion = new ActivityNoteConversion(activities, comments, service, new ListLogger<ActivityNoteConversion>());

        // A host that started on this storage already ran the conversion on its empty library and stored the marker.
        var markerAtStart = await raw.GetEntityIfExistsAsync<TableEntity>(
            ActivityNoteConversion.MarkerPartitionKey, ActivityNoteConversion.MarkerRowKey, cancellationToken: ct);
        TestContext.WriteLine($"INFO marker before seeding: {(markerAtStart.HasValue ? "present (host-start pass done)" : "absent")}");
        if (apiUrl is not null)
            Flow(markerAtStart.HasValue, "host-start pass stored the marker", "the API host ran the conversion at start");

        // Old-style data: two Activities with an old note and one without, written straight to the Activities table.
        var gameId = Guid.NewGuid();
        var first = new ActivityEntity { GameId = gameId, Name = "Old note one", Comments = "  Ran long, cut the second round  " };
        var second = new ActivityEntity { GameId = gameId, Name = "Old note two", Comments = "Bring snacks" };
        var none = new ActivityEntity { GameId = gameId, Name = "No note", Comments = null };
        foreach (var activity in new[] { first, second, none })
            await activities.UpsertAsync(activity, ct);
        var seeded = new Dictionary<Guid, ActivityEntity>();
        foreach (var activity in new[] { first, second, none })
            seeded[activity.Id] = (await activities.GetAsync(activity.Id, ct))!;
        TestContext.WriteLine($"INFO seeded Activities {first.Id} (note), {second.Id} (note), {none.Id} (no note)");

        if (apiUrl is not null)
        {
            var before = 0;
            foreach (var id in seeded.Keys) before += (await ListAsync(apiUrl, id, ct)).Count;
            Flow(before == 0, "list route before the conversion", $"{before} Comments for the seeded Activities");
        }

        // The upgrade case: the marker is removed so this pass sees the old notes, as a first start over old data would.
        await raw.DeleteEntityAsync(ActivityNoteConversion.MarkerPartitionKey, ActivityNoteConversion.MarkerRowKey, cancellationToken: ct);
        await conversion.RunAsync(ct);

        var stored = await CommentsOfAsync(comments, seeded.Keys, ct);
        Flow(stored.Count == 2, "first run, table read-back", $"{stored.Count} Comments for the seeded Activities");
        foreach (var activity in new[] { seeded[first.Id], seeded[second.Id] })
        {
            var comment = stored.Single(c => c.ActivityId == activity.Id);
            var expected = activity.Comments!.Trim();
            var ok = comment.Id == ActivityNoteConversion.CommentIdFor(activity.Id) && comment.Text == expected
                && comment.CreatedUtc == activity.Timestamp && comment.EventId is null;
            Flow(ok, $"Comment of {activity.Id}",
                $"id {comment.Id}, text \"{comment.Text}\", createdUtc {comment.CreatedUtc:O} (row Timestamp {activity.Timestamp:O}), eventId {comment.EventId?.ToString() ?? "null"}");
        }
        Flow(stored.All(c => c.ActivityId != none.Id), "Activity without a note", "has no Comment");
        var markerAfter = await raw.GetEntityIfExistsAsync<TableEntity>(
            ActivityNoteConversion.MarkerPartitionKey, ActivityNoteConversion.MarkerRowKey, cancellationToken: ct);
        Flow(markerAfter.HasValue, "marker after the pass", "stored in ActivityComments, partition Migration");
        var note = (await activities.GetAsync(first.Id, ct))!.Comments;
        Flow(note == first.Comments, "old field untouched", $"Activity.Comments is still \"{note}\"");

        if (apiUrl is not null)
        {
            foreach (var activity in new[] { seeded[first.Id], seeded[second.Id] })
            {
                var listed = await ListAsync(apiUrl, activity.Id, ct);
                var ok = listed.Count == 1 && listed[0].Text == activity.Comments!.Trim()
                    && listed[0].CreatedUtc == activity.Timestamp && listed[0].EventId is null;
                Flow(ok, $"list route GET {apiUrl}/activities/{activity.Id}/comments",
                    string.Join("; ", listed.Select(c => $"text \"{c.Text}\", createdUtc {c.CreatedUtc:O}, eventId {c.EventId?.ToString() ?? "null"}")));
            }
            var listedNone = await ListAsync(apiUrl, none.Id, ct);
            Flow(listedNone.Count == 0, "list route, Activity without a note", $"{listedNone.Count} Comments");
        }

        // Second run: the marker is there, so nothing is added.
        await conversion.RunAsync(ct);
        var afterSecond = await CommentsOfAsync(comments, seeded.Keys, ct);
        Flow(afterSecond.Count == 2, "second run", $"{afterSecond.Count} Comments, unchanged");

        // Third run with the marker removed again: the deterministic ids still add no duplicate in real storage.
        await raw.DeleteEntityAsync(ActivityNoteConversion.MarkerPartitionKey, ActivityNoteConversion.MarkerRowKey, cancellationToken: ct);
        await conversion.RunAsync(ct);
        var afterThird = await CommentsOfAsync(comments, seeded.Keys, ct);
        Flow(afterThird.Count == 2, "rerun without the marker", $"{afterThird.Count} Comments, no duplicate (insert refused as EntityAlreadyExists)");

        if (apiUrl is not null)
        {
            var total = 0;
            foreach (var id in seeded.Keys) total += (await ListAsync(apiUrl, id, ct)).Count;
            Flow(total == 2, "list route after the reruns", $"{total} Comments for the seeded Activities");
        }
    }

    private static async Task<List<CommentEntity>> CommentsOfAsync(
        TableStore<CommentEntity> comments, IEnumerable<Guid> activityIds, CancellationToken ct)
    {
        var ids = activityIds.ToHashSet();
        return (await comments.GetAllAsync(ct)).Where(c => ids.Contains(c.ActivityId)).ToList();
    }

    private sealed record ListedComment(string Text, DateTimeOffset CreatedUtc, Guid? EventId);

    private static async Task<List<ListedComment>> ListAsync(string apiUrl, Guid activityId, CancellationToken ct)
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync($"{apiUrl}/activities/{activityId}/comments", ct);
        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode, $"GET comments of {activityId}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.EnumerateArray().Select(c => new ListedComment(
            c.GetProperty("text").GetString()!,
            c.GetProperty("createdUtc").GetDateTimeOffset(),
            c.GetProperty("eventId").ValueKind == JsonValueKind.Null ? null : c.GetProperty("eventId").GetGuid())).ToList();
    }
}
