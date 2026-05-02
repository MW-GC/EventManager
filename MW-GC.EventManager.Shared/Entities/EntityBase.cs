using Azure;
using Azure.Data.Tables;

namespace MW_GC.EventManager.Shared.Entities;

/// <summary>
/// Shared base for all table entities. Carries a typed <see cref="Id"/> (Guid)
/// and handles the <see cref="ITableEntity.RowKey"/> ↔ <see cref="Id"/> boundary.
/// </summary>
public abstract class EntityBase : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;
    public string RowKey { get => Id.ToString("D"); set => Id = Guid.Parse(value); }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public Guid Id { get; set; } = Guid.NewGuid();
}
