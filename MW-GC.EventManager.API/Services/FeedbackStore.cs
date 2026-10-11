using Azure;
using Azure.Data.Tables;

namespace MW_GC.EventManager.API.Services;

/// <summary>
/// Storage for the anonymous Event feedback page (#101), kept OUT of the Events table on purpose:
/// an admin PUT of an Event replaces the whole row and would reset anything stored on it.
/// <para>
/// Table <c>FeedbackLinks</c> holds two rows per Event link, both in one partition each:
/// <c>Event/{eventId}</c> with <c>Token</c>, and <c>Token/{token}</c> with <c>EventId</c>. A token is
/// valid only while the Event row names it, so a token row left behind by a lost race or a crash is dead.
/// </para>
/// <para>
/// Table <c>FeedbackLikes</c> holds one row per like: PartitionKey is the Event id, RowKey is
/// <see cref="FeedbackTokens.LikeKey"/> of the visitor's address. No other property is written, so the
/// row says nothing about the person beyond that salted hash (and the Timestamp storage adds). The
/// insert is insert-if-absent, so two simultaneous likes from one address count once.
/// </para>
/// </summary>
internal sealed class FeedbackStore
{
    public const string LinksTable = "FeedbackLinks";
    public const string LikesTable = "FeedbackLikes";
    public const string EventPartition = "Event";
    public const string TokenPartition = "Token";
    public const string TokenProperty = "Token";
    public const string EventIdProperty = "EventId";

    private readonly TableClient _links;
    private readonly TableClient _likes;

    public FeedbackStore(TableServiceClient serviceClient)
    {
        _links = serviceClient.GetTableClient(LinksTable);
        _links.CreateIfNotExists();
        _likes = serviceClient.GetTableClient(LikesTable);
        _likes.CreateIfNotExists();
    }

    /// <summary>The Event's token, created the first time it is asked for. Concurrent first calls agree on one token.</summary>
    public async Task<string> GetOrCreateTokenAsync(Guid eventId, CancellationToken ct = default)
    {
        if (await ReadTokenAsync(eventId, ct) is { } existing) return existing;

        var token = FeedbackTokens.Create();
        // The token row goes first. If the Event row below is never written, the token row is
        // dead: FindEventIdAsync only accepts a token the Event row names.
        await _links.AddEntityAsync(new TableEntity(TokenPartition, token) { [EventIdProperty] = eventId }, ct);
        try
        {
            await _links.AddEntityAsync(new TableEntity(EventPartition, EventKey(eventId)) { [TokenProperty] = token }, ct);
            return token;
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            // Another request created this Event's link first: use that one and drop the unused row.
            try { await _links.DeleteEntityAsync(TokenPartition, token, ETag.All, ct); }
            catch (RequestFailedException) { /* a dead row; FindEventIdAsync never accepts it */ }
            return await ReadTokenAsync(eventId, ct)
                ?? throw new InvalidOperationException("The Event's feedback link disappeared while it was being created.");
        }
    }

    /// <summary>The Event a token belongs to, or null for a malformed, unknown or dead token.</summary>
    public async Task<Guid?> FindEventIdAsync(string token, CancellationToken ct = default)
    {
        if (!FeedbackTokens.IsWellFormed(token)) return null;
        var row = await GetAsync(_links, TokenPartition, token, ct);
        if (row?.GetGuid(EventIdProperty) is not { } eventId) return null;
        return await ReadTokenAsync(eventId, ct) == token ? eventId : null;
    }

    /// <summary>Records one like. False when this address had already liked the Event (nothing changes).</summary>
    public async Task<bool> TryAddLikeAsync(Guid eventId, string token, string clientIp, CancellationToken ct = default)
    {
        try
        {
            await _likes.AddEntityAsync(new TableEntity(EventKey(eventId), FeedbackTokens.LikeKey(token, clientIp)), ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 409 && ex.ErrorCode == "EntityAlreadyExists")
        {
            return false;
        }
    }

    public async Task<int> CountLikesAsync(Guid eventId, CancellationToken ct = default)
    {
        var partition = EventKey(eventId);
        var count = 0;
        await foreach (var _ in _likes.QueryAsync<TableEntity>(e => e.PartitionKey == partition, select: ["RowKey"], cancellationToken: ct))
            count++;
        return count;
    }

    private async Task<string?> ReadTokenAsync(Guid eventId, CancellationToken ct) =>
        (await GetAsync(_links, EventPartition, EventKey(eventId), ct))?.GetString(TokenProperty);

    private static string EventKey(Guid eventId) => eventId.ToString("D");

    private static async Task<TableEntity?> GetAsync(TableClient table, string partitionKey, string rowKey, CancellationToken ct)
    {
        try
        {
            return (await table.GetEntityAsync<TableEntity>(partitionKey, rowKey, cancellationToken: ct)).Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }
}
