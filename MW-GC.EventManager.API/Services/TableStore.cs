using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.API.Services;

/// <summary>The outcome of a conditional update.</summary>
internal enum UpdateOutcome
{
    Updated,

    /// <summary>The row does not exist (or was deleted after the caller read it). Nothing was written.</summary>
    NotFound,

    /// <summary>The row exists but its ETag no longer matches If-Match. Nothing was written.</summary>
    Conflict,
}

/// <summary>The outcome of a conditional delete.</summary>
internal enum DeleteOutcome
{
    Deleted,

    /// <summary>The row was already gone. DELETE is idempotent, so callers treat this as a success.</summary>
    Missing,

    /// <summary>The row exists but its ETag no longer matches If-Match. Nothing was deleted.</summary>
    Conflict,
}

/// <summary>
/// A complex property that is too large to store even when split into chunks: the row would pass
/// Table Storage's 1 MiB entity limit or its 252 custom-property limit. Nothing is written.
/// </summary>
internal sealed class StoredValueTooLargeException(string property)
    : Exception($"{property} is too large to store.")
{
    public string Property { get; } = property;
}

/// <summary>
/// Single-partition CRUD over Azure Table Storage.
/// Handles the boundary between typed entity properties and Table Storage's flat scalar model:
/// any property that isn't a natively supported type gets JSON-serialized on write and
/// deserialized on read. Consumers never deal with that.
/// A serialised value longer than <see cref="ChunkLength"/> characters is split over several
/// properties (<c>Selections</c>, <c>Selections__1</c>, <c>Selections__2</c> ...) and joined again
/// on read, so no stored string passes Table Storage's per-property limit (64 KiB of UTF-16,
/// about 32K characters). A row written before chunking existed (one property) reads back unchanged.
/// Every write reports the row's new ETag on the entity; updates and deletes are conditional.
/// </summary>
internal sealed class TableStore<TEntity> where TEntity : EntityBase, new()
{
    /// <summary>Longest string written to one property. Table Storage allows 32,768 UTF-16 characters (64 KiB).</summary>
    internal const int ChunkLength = 30_000;

    /// <summary>Separator between a property name and its chunk number.</summary>
    internal const string ChunkSeparator = "__";

    /// <summary>Table Storage's limit on all data in one entity (1 MiB).</summary>
    internal const long MaxEntityBytes = 1024 * 1024;

    /// <summary>Table Storage allows 255 properties per entity, 3 of them system properties.</summary>
    internal const int MaxCustomProperties = 252;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Tolerate differently-cased property names so data written by earlier
        // versions of the app still deserializes on read. Writes stay camelCase.
        PropertyNameCaseInsensitive = true,
        // Store non-ASCII text as the character, not as a 6-character \uXXXX escape. This JSON is
        // only ever read back by this class; it never reaches HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly HashSet<Type> NativeTypes =
    [
        typeof(string), typeof(bool), typeof(bool?),
        typeof(int), typeof(int?), typeof(long), typeof(long?),
        typeof(double), typeof(double?),
        typeof(Guid), typeof(Guid?),
        typeof(DateTimeOffset), typeof(DateTimeOffset?),
        typeof(byte[])
    ];

    private readonly TableClient _table;
    private readonly string _partitionKey;

    /// <summary>Complex (non-native) properties that need JSON serialization.</summary>
    private static readonly PropertyInfo[] ComplexProps = typeof(TEntity)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite
            && !NativeTypes.Contains(p.PropertyType)
            && p.Name is not (nameof(EntityBase.PartitionKey) or nameof(EntityBase.RowKey)
                or nameof(EntityBase.Timestamp) or nameof(EntityBase.ETag) or nameof(EntityBase.Id)))
        .ToArray();

    public TableStore(TableServiceClient serviceClient, string tableName, string partitionKey)
    {
        _table = serviceClient.GetTableClient(tableName);
        _table.CreateIfNotExists();
        _partitionKey = partitionKey;
    }

    public async Task<List<TEntity>> GetAllAsync(CancellationToken ct = default)
    {
        if (ComplexProps.Length == 0)
        {
            var results = new List<TEntity>();
            await foreach (var entity in _table.QueryAsync<TEntity>(e => e.PartitionKey == _partitionKey, cancellationToken: ct))
                results.Add(entity);
            return results;
        }

        // When complex props exist, read as TableEntity and hydrate manually.
        var raw = new List<TableEntity>();
        await foreach (var row in _table.QueryAsync<TableEntity>(e => e.PartitionKey == _partitionKey, cancellationToken: ct))
            raw.Add(row);
        return raw.Select(Hydrate).ToList();
    }

    public async Task<TEntity?> GetAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            if (ComplexProps.Length == 0)
            {
                var response = await _table.GetEntityAsync<TEntity>(_partitionKey, id.ToString("D"), cancellationToken: ct);
                return response.Value;
            }

            var raw = await _table.GetEntityAsync<TableEntity>(_partitionKey, id.ToString("D"), cancellationToken: ct);
            return Hydrate(raw.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary>Inserts or replaces a row. Only create paths use this; updates go through <see cref="UpdateAsync"/>.</summary>
    public async Task UpsertAsync(TEntity entity, CancellationToken ct = default)
    {
        entity.PartitionKey = _partitionKey;

        var response = ComplexProps.Length == 0
            ? await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct)
            : await _table.UpsertEntityAsync(Flatten(entity), TableUpdateMode.Replace, ct);
        entity.ETag = NewETag(response);
    }

    /// <summary>Atomically inserts without replacing an existing row, including on concurrent retries.</summary>
    public async Task<bool> TryAddAsync(TEntity entity, CancellationToken ct = default)
    {
        entity.PartitionKey = _partitionKey;
        try
        {
            var response = ComplexProps.Length == 0
                ? await _table.AddEntityAsync(entity, ct)
                : await _table.AddEntityAsync(Flatten(entity), ct);
            entity.ETag = NewETag(response);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 409 && ex.ErrorCode == "EntityAlreadyExists")
        {
            return false;
        }
    }

    /// <summary>
    /// Replaces an EXISTING row, never creating one. <paramref name="ifMatch"/> is passed to storage
    /// as given; <see cref="ETag.All"/> (or a default ETag) means "any existing row". A row that is
    /// gone is <see cref="UpdateOutcome.NotFound"/>, a stale tag is <see cref="UpdateOutcome.Conflict"/>,
    /// and in both cases nothing is written. On success the entity carries the row's new ETag.
    /// </summary>
    public async Task<UpdateOutcome> UpdateAsync(TEntity entity, ETag ifMatch, CancellationToken ct = default)
    {
        entity.PartitionKey = _partitionKey;
        if (ifMatch == default) ifMatch = ETag.All;
        try
        {
            var response = ComplexProps.Length == 0
                ? await _table.UpdateEntityAsync(entity, ifMatch, TableUpdateMode.Replace, ct)
                : await _table.UpdateEntityAsync(Flatten(entity), ifMatch, TableUpdateMode.Replace, ct);
            entity.ETag = NewETag(response);
            return UpdateOutcome.Updated;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return UpdateOutcome.NotFound;
        }
        catch (RequestFailedException ex) when (ex.Status == 412)
        {
            return UpdateOutcome.Conflict;
        }
    }

    /// <summary>
    /// Deletes a row. A missing row is <see cref="DeleteOutcome.Missing"/> (DELETE is idempotent);
    /// an existing row whose ETag does not match <paramref name="ifMatch"/> is <see cref="DeleteOutcome.Conflict"/>.
    /// A default ETag means "any existing row".
    /// </summary>
    public async Task<DeleteOutcome> DeleteAsync(Guid id, ETag ifMatch = default, CancellationToken ct = default)
    {
        if (ifMatch == default) ifMatch = ETag.All;
        try
        {
            // The SDK returns a missing row's 404 as a response rather than throwing it.
            var response = await _table.DeleteEntityAsync(_partitionKey, id.ToString("D"), ifMatch, ct);
            return response?.Status == 404 ? DeleteOutcome.Missing : DeleteOutcome.Deleted;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return DeleteOutcome.Missing;
        }
        catch (RequestFailedException ex) when (ex.Status == 412)
        {
            return DeleteOutcome.Conflict;
        }
    }

    // The row's new ETag, from the ETag header of the write response (the same string a read
    // returns as odata.etag).
    private static ETag NewETag(Response? response) => response?.Headers.ETag ?? default;

    private static TableEntity Flatten(TEntity entity)
    {
        // Flatten complex properties to JSON strings, split into chunks when they are long.
        var row = new TableEntity(entity.PartitionKey, entity.RowKey);
        string? largest = null;
        var largestLength = -1;
        foreach (var prop in typeof(TEntity).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.Name is nameof(EntityBase.PartitionKey) or nameof(EntityBase.RowKey)
                or nameof(EntityBase.Timestamp) or nameof(EntityBase.ETag) or nameof(EntityBase.Id))
                continue;
            if (!prop.CanRead) continue;

            var value = prop.GetValue(entity);
            if (ComplexProps.Contains(prop))
            {
                var json = JsonSerializer.Serialize(value, prop.PropertyType, Json);
                WriteChunks(row, prop.Name, json);
                if (json.Length > largestLength)
                {
                    largest = prop.Name;
                    largestLength = json.Length;
                }
            }
            else
                row[prop.Name] = value;
        }

        // Refuse a row Table Storage would refuse, naming the largest serialised property.
        if (largest is not null && (CustomProperties(row) > MaxCustomProperties || EstimatedBytes(row) > MaxEntityBytes))
            throw new StoredValueTooLargeException(largest);
        return row;
    }

    private static void WriteChunks(TableEntity row, string name, string json)
    {
        var start = 0;
        var index = 0;
        do
        {
            var length = Math.Min(ChunkLength, json.Length - start);
            // Never split a surrogate pair across two properties.
            if (start + length < json.Length && char.IsHighSurrogate(json[start + length - 1]))
                length--;
            row[ChunkName(name, index)] = json.Substring(start, length);
            start += length;
            index++;
        }
        while (start < json.Length);
    }

    private static string ChunkName(string name, int index) => index == 0 ? name : $"{name}{ChunkSeparator}{index}";

    /// <summary>Joins a property and its chunks (<c>Name</c>, <c>Name__1</c>, ...) back into one string.</summary>
    private static string? ReadChunks(TableEntity row, string name)
    {
        var first = row.GetString(name);
        if (first is null || !row.ContainsKey(ChunkName(name, 1))) return first;

        var json = new System.Text.StringBuilder(first);
        for (var index = 1; row.TryGetValue(ChunkName(name, index), out var chunk) && chunk is string text; index++)
            json.Append(text);
        return json.ToString();
    }

    /// <summary>
    /// The entity size as Table Storage counts it: 4 bytes, the keys as UTF-16, and per property
    /// 8 bytes plus its name as UTF-16 plus its value (strings as 4 bytes plus UTF-16).
    /// </summary>
    internal static long EstimatedBytes(TableEntity row)
    {
        long size = 4 + (row.PartitionKey.Length + row.RowKey.Length) * 2L;
        foreach (var (name, value) in row)
        {
            if (IsSystemKey(name)) continue;
            size += 8 + name.Length * 2L + value switch
            {
                null => 0,
                string text => 4 + text.Length * 2L,
                byte[] bytes => 4 + bytes.Length,
                bool => 1,
                int => 4,
                Guid => 16,
                _ => 8,
            };
        }
        return size;
    }

    private static int CustomProperties(TableEntity row) => row.Keys.Count(name => !IsSystemKey(name));

    private static bool IsSystemKey(string name) => name is "PartitionKey" or "RowKey" or "Timestamp" or "odata.etag";

    /// <summary>Reads a raw <see cref="TableEntity"/> back into a typed <typeparamref name="TEntity"/>.</summary>
    private static TEntity Hydrate(TableEntity row)
    {
        var entity = new TEntity
        {
            PartitionKey = row.PartitionKey,
            Id = Guid.Parse(row.RowKey),
            Timestamp = row.Timestamp,
            ETag = row.ETag
        };

        foreach (var prop in typeof(TEntity).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.Name is nameof(EntityBase.PartitionKey) or nameof(EntityBase.RowKey)
                or nameof(EntityBase.Timestamp) or nameof(EntityBase.ETag) or nameof(EntityBase.Id))
                continue;
            if (!prop.CanWrite || !row.ContainsKey(prop.Name)) continue;

            if (ComplexProps.Contains(prop))
            {
                var json = ReadChunks(row, prop.Name);
                if (json is not null)
                    prop.SetValue(entity, JsonSerializer.Deserialize(json, prop.PropertyType, Json));
            }
            else
            {
                var raw = row[prop.Name];
                if (raw is not null)
                    prop.SetValue(entity, Convert.ChangeType(raw, Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType));
            }
        }

        return entity;
    }
}
