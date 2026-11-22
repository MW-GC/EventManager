using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Moq;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// Shared set-up of the Comment route tests (#129, ADR 0001): one in-memory library with a Game, and the
/// request, seeding and counting helpers.
/// </summary>
public abstract class CommentApiTestBase
{
    private protected const string TextRequired = "Comment text is required.";
    private protected const string TextTooLong = "Comment text must be 1000 characters or fewer.";
    private protected const string EventNotFound = "Event not found.";

    private protected readonly LibraryHarness h = new();
    private protected readonly GameEntity game;

    private protected CommentApiTestBase() => game = h.SeedGame("Owner");

    // ---- helpers

    private protected static HttpRequest Request(object body, string? ifMatch = null)
    {
        var request = TestRequests.Json(body);
        if (ifMatch is not null) request.Headers["If-Match"] = ifMatch;
        return request;
    }

    private protected static HttpRequest Bare(string? ifMatch = null)
    {
        var request = TestRequests.Empty();
        if (ifMatch is not null) request.Headers["If-Match"] = ifMatch;
        return request;
    }

    private protected static string? ETagHeader(HttpRequest request) =>
        request.HttpContext.Response.Headers.ETag.Count == 0 ? null : request.HttpContext.Response.Headers.ETag.ToString();

    private protected async Task<(Guid Id, string ETag)> CreateActivity(string name = "Act")
    {
        var request = Request(new { name, gameId = game.Id });
        var result = await h.ActivityApi.Create(request, default);
        Assert.AreEqual(StatusCodes.Status201Created, ApiResults.Status(result));
        return (ApiResults.Value<ActivityEntity>(result).Id, ETagHeader(request)!);
    }

    private protected Guid SeedEvent()
    {
        var id = Guid.NewGuid().ToString("D");
        h.Events[id] = new TableEntity("Event", id) { ["Name"] = "Friday", ["Selections"] = "[]", ["UniqueGamesOnly"] = true };
        return Guid.Parse(id);
    }

    private protected async Task<(CommentEntity Comment, string ETag)> AddComment(Guid activityId, string text, Guid? eventId = null)
    {
        var request = Request(new { text, eventId });
        var result = await h.CommentApi.Add(request, activityId, default);
        Assert.AreEqual(StatusCodes.Status201Created, ApiResults.Status(result));
        return (ApiResults.Value<CommentEntity>(result), ETagHeader(request)!);
    }

    // A Comment written straight into the in-memory table, for a chosen added date or id.
    private protected CommentEntity Seed(Guid activityId, string text, DateTimeOffset createdUtc, Guid? id = null)
    {
        var comment = new CommentEntity
        {
            Id = id ?? Guid.NewGuid(), ActivityId = activityId, Text = text,
            CreatedUtc = createdUtc, ETag = TableMocks.NextETag(),
        };
        h.SeedComment(comment);
        return comment;
    }

    private protected async Task<List<CommentEntity>> List(Guid activityId) =>
        ApiResults.Value<List<CommentEntity>>(await h.CommentApi.List(Bare(), activityId, default));

    private protected static int Calls(Mock<TableClient> table, string method) => table.Invocations.Count(i => i.Method.Name == method);

    private protected int CommentWrites() =>
        Calls(h.CommentTable, nameof(TableClient.UpsertEntityAsync)) + Calls(h.CommentTable, nameof(TableClient.AddEntityAsync))
        + Calls(h.CommentTable, nameof(TableClient.UpdateEntityAsync));
}
