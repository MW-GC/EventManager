using System.Collections.Concurrent;
using System.Linq.Expressions;
using Azure;
using Azure.Data.Tables;
using Moq;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// <see cref="TableMocks"/>, continued: a whole table whose rows of several partitions share one
/// dictionary, for the Comment table and the old-text conversion's marker.
/// </summary>
internal static partial class TableMocks
{
    /// <summary>The key of a row in a <see cref="PartitionedTable{T}"/>: its partition and row key.</summary>
    public static string Key(string partitionKey, string rowKey) => $"{partitionKey}|{rowKey}";

    /// <summary>
    /// A whole table in one dictionary keyed by partition AND row key (<see cref="Key"/>), as storage
    /// keys it: typed <typeparamref name="T"/> rows and plain <see cref="TableEntity"/> rows of other
    /// partitions (a marker, say) side by side. Writes behave as in <see cref="Table{T}"/>. A typed
    /// query evaluates its filter over EVERY row, so a query without the partition condition reaches
    /// the other partitions' rows too: a plain row is read as a <typeparamref name="T"/> (its partition
    /// and the columns <typeparamref name="T"/> has), and one whose row key is not a Guid makes the
    /// query throw, as the SDK does when it cannot read that row into the type.
    /// <paramref name="rowsRead"/> is told how many rows each query answered with.
    /// </summary>
    public static Mock<TableClient> PartitionedTable<T>(ConcurrentDictionary<string, ITableEntity> rows, Action<int>? rowsRead = null)
        where T : class, ITableEntity, new()
    {
        var table = new Mock<TableClient>();
        table.Setup(t => t.UpsertEntityAsync(It.IsAny<T>(), TableUpdateMode.Replace, It.IsAny<CancellationToken>()))
            .Returns((T row, TableUpdateMode _, CancellationToken _) => Task.FromResult(StoreKeyed(rows, row)));
        table.Setup(t => t.AddEntityAsync(It.IsAny<T>(), It.IsAny<CancellationToken>()))
            .Returns((T row, CancellationToken _) => Task.FromResult(AddRow(rows, row)));
        table.Setup(t => t.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>()))
            .Returns((TableEntity row, CancellationToken _) => Task.FromResult(AddRow(rows, row)));
        table.Setup(t => t.UpdateEntityAsync(It.IsAny<T>(), It.IsAny<ETag>(), TableUpdateMode.Replace, It.IsAny<CancellationToken>()))
            .Returns((T row, ETag ifMatch, TableUpdateMode _, CancellationToken _) =>
            {
                CheckKeyed(rows, Key(row.PartitionKey, row.RowKey), ifMatch);
                return Task.FromResult(StoreKeyed(rows, row));
            });
        table.Setup(t => t.DeleteEntityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ETag>(), It.IsAny<CancellationToken>()))
            .Returns((string partition, string id, ETag ifMatch, CancellationToken ct) =>
            {
                CheckKeyed(rows, Key(partition, id), ifMatch);
                rows.TryRemove(Key(partition, id), out _);
                return Task.FromResult<Response>(new StorageResponse());
            });
        table.Setup(t => t.GetEntityAsync<T>(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string partition, string id, IEnumerable<string> _, CancellationToken _) =>
                rows.TryGetValue(Key(partition, id), out var row) && row is T typed
                    ? Response.FromValue(typed, Mock.Of<Response>())
                    : throw new RequestFailedException(404, "The specified resource does not exist.", "ResourceNotFound", null));
        table.Setup(t => t.GetEntityAsync<TableEntity>(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string partition, string id, IEnumerable<string> _, CancellationToken _) =>
                rows.TryGetValue(Key(partition, id), out var row) && row is TableEntity plain
                    ? Response.FromValue(plain, Mock.Of<Response>())
                    : throw new RequestFailedException(404, "The specified resource does not exist.", "ResourceNotFound", null));
        table.Setup(t => t.QueryAsync(It.IsAny<Expression<Func<T, bool>>>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns((Expression<Func<T, bool>> filter, int? _, IEnumerable<string> _, CancellationToken _) =>
            {
                var matches = Query(rows, filter.Compile());
                rowsRead?.Invoke(matches.Count);
                return AsyncPageable<T>.FromPages([Page<T>.FromValues(matches, null, Mock.Of<Response>())]);
            });
        return table;
    }

    /// <summary>
    /// Inserts a row into a <see cref="PartitionedTable{T}"/> as storage does: a fresh ETag, and a 409
    /// <c>EntityAlreadyExists</c> when its partition and row key are taken. For tests that wrap the insert.
    /// </summary>
    public static Response AddRow(ConcurrentDictionary<string, ITableEntity> rows, ITableEntity row)
    {
        if (!rows.TryAdd(Key(row.PartitionKey, row.RowKey), row))
            throw new RequestFailedException(409, "Entity already exists", "EntityAlreadyExists", null);
        row.ETag = NextETag();
        return new StorageResponse(row.ETag);
    }

    private static List<T> Query<T>(ConcurrentDictionary<string, ITableEntity> rows, Func<T, bool> filter) where T : class, ITableEntity, new()
    {
        var matches = new List<T>();
        foreach (var row in rows.Values)
        {
            if (row is T typed)
            {
                if (filter(typed)) matches.Add(typed);
                continue;
            }

            // A plain row of another partition, read as T would read it: its partition and the columns T has.
            var plain = (TableEntity)row;
            var read = new T { PartitionKey = plain.PartitionKey };
            foreach (var prop in typeof(T).GetProperties())
                if (prop.CanWrite && prop.Name is not (nameof(ITableEntity.PartitionKey) or nameof(ITableEntity.RowKey))
                    && plain.TryGetValue(prop.Name, out var value) && value is not null && prop.PropertyType.IsInstanceOfType(value))
                    prop.SetValue(read, value);
            if (!filter(read)) continue;
            // Reached by the query: the SDK must now read its row key into T, which fails for a non-Guid key.
            read.RowKey = plain.RowKey;
            matches.Add(read);
        }
        return matches;
    }

    private static Response StoreKeyed(ConcurrentDictionary<string, ITableEntity> rows, ITableEntity row)
    {
        row.ETag = NextETag();
        rows[Key(row.PartitionKey, row.RowKey)] = row;
        return new StorageResponse(row.ETag);
    }

    private static void CheckKeyed(ConcurrentDictionary<string, ITableEntity> rows, string key, ETag ifMatch)
    {
        if (!rows.TryGetValue(key, out var current))
            throw new RequestFailedException(404, "The specified resource does not exist.", "ResourceNotFound", null);
        if (ifMatch != ETag.All && ifMatch != current.ETag)
            throw new RequestFailedException(412, "The update condition specified in the request was not satisfied.", "UpdateConditionNotSatisfied", null);
    }
}
