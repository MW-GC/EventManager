using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// <see cref="TableStoreRegistration.AddTableStores"/>, the registrations <c>Program.cs</c> uses, over a
/// mocked <see cref="TableServiceClient"/>: no storage account is needed, because a store only asks the
/// client for its table and creates it.
/// </summary>
[TestClass]
public class TableStoreRegistrationTests
{
    [TestMethod]
    public async Task EachOfTheSevenTablesGetsOneStoreUnderItsStoredTableAndPartitionName()
    {
        // Every table client the stores ask for, by table name.
        var clients = new Dictionary<string, Mock<TableClient>>();
        var service = new Mock<TableServiceClient>();
        service.Setup(s => s.GetTableClient(It.IsAny<string>())).Returns((string name) =>
        {
            Assert.IsFalse(clients.ContainsKey(name), $"The {name} table was asked for twice.");
            clients[name] = new Mock<TableClient>();
            return clients[name].Object;
        });
        await using var provider = new ServiceCollection().AddSingleton(service.Object).AddTableStores().BuildServiceProvider();

        // Resolves the store and writes one row through it: the table it asked for, and the partition it wrote to.
        async Task<(string Table, string Partition)> Probe<T>() where T : EntityBase, new()
        {
            var before = clients.Keys.ToHashSet();
            var store = provider.GetRequiredService<TableStore<T>>();
            var table = Assert.ContainsSingle(clients.Keys.Except(before));
            clients[table].Verify(t => t.CreateIfNotExists(It.IsAny<CancellationToken>()), Times.Once());
            var row = new T { Id = Guid.NewGuid() };
            await store.UpsertAsync(row);
            Assert.ContainsSingle(clients[table].Invocations.Where(i => i.Method.Name == nameof(TableClient.UpsertEntityAsync)),
                $"The {typeof(T).Name} store writes to the table it asked for.");
            return (table, row.PartitionKey);
        }

        var registered = new[]
        {
            await Probe<GameEntity>(),
            await Probe<ActivityEntity>(),
            await Probe<EventEntity>(),
            await Probe<ThemeEntity>(),
            await Probe<HolidayEntity>(),
            await Probe<EventMetricsEntity>(),
            await Probe<CommentEntity>(),
        };

        CollectionAssert.AreEqual(StoredNames.Stores, registered);
        Assert.HasCount(7, clients, "No other table is asked for.");
    }
}
