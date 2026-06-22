using System.Reflection;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.API.Services;

/// <summary>
/// Single-partition CRUD over Azure Table Storage.
/// Handles the boundary between typed entity properties and Table Storage's flat scalar model:
/// any property that isn't a natively supported type gets JSON-serialized on write and
/// deserialized on read. Consumers never deal with that.
/// </summary>
internal sealed class TableStore<TEntity> where TEntity : EntityBase, new()
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Tolerate differently-cased property names so data written by earlier
        // versions of the app still deserializes on read/import. Writes stay camelCase.
        PropertyNameCaseInsensitive = true
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

    public async Task UpsertAsync(TEntity entity, CancellationToken ct = default)
    {
        entity.PartitionKey = _partitionKey;

        if (ComplexProps.Length == 0)
        {
            await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
            return;
        }

        // Flatten complex properties to JSON strings.
        var row = new TableEntity(entity.PartitionKey, entity.RowKey);
        foreach (var prop in typeof(TEntity).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.Name is nameof(EntityBase.PartitionKey) or nameof(EntityBase.RowKey)
                or nameof(EntityBase.Timestamp) or nameof(EntityBase.ETag) or nameof(EntityBase.Id))
                continue;
            if (!prop.CanRead) continue;

            var value = prop.GetValue(entity);
            if (ComplexProps.Contains(prop))
                row[prop.Name] = JsonSerializer.Serialize(value, prop.PropertyType, Json);
            else
                row[prop.Name] = value;
        }
        await _table.UpsertEntityAsync(row, TableUpdateMode.Replace, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            await _table.DeleteEntityAsync(_partitionKey, id.ToString("D"), cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Already gone — DELETE is idempotent, so a missing row is a success.
        }
    }

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
                var json = row.GetString(prop.Name);
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
