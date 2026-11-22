using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>The old-text conversion started with the host (#130): it runs in the background, and a failure never stops the host.</summary>
[TestClass]
public class ActivityNoteConversionStartupTests : ActivityNoteConversionTestBase
{
    [TestMethod]
    public async Task TheStartupServiceRunsTheConversion()
    {
        var activityId = SeedActivity("Converted at start");
        await using var services = HostServices();
        var startup = new ActivityNoteConversionStartup(services, new ListLogger<ActivityNoteConversionStartup>());

        await startup.StartAsync(default);
        await startup.ExecuteTask!;

        Assert.ContainsSingle(CommentsOf(activityId));
        Assert.IsTrue(h.MarkerStored());
    }

    [TestMethod]
    public async Task AFailedConversionIsLoggedAndNeverStopsTheHost()
    {
        SeedActivity("Will fail");
        var outage = new RequestFailedException(503, "Storage unavailable");
        h.CommentTable.Setup(t => t.AddEntityAsync(It.IsAny<CommentEntity>(), It.IsAny<CancellationToken>())).ThrowsAsync(outage);
        var log = new ListLogger<ActivityNoteConversionStartup>();
        await using var services = HostServices();
        var startup = new ActivityNoteConversionStartup(services, log);

        await startup.StartAsync(default);
        // Awaiting the hosted service's task throws when the failure faulted it.
        await startup.ExecuteTask!;

        var entry = Assert.ContainsSingle(log.Entries);
        Assert.AreEqual(LogLevel.Error, entry.Level);
        Assert.AreSame(outage, entry.Exception);
        Assert.IsFalse(h.MarkerStored());
        await startup.StopAsync(default);
    }

    [TestMethod]
    public async Task AStoreThatFailsWhileTheHostBuildsItIsLoggedAndNeverStopsTheHost()
    {
        // Registered as Program.cs registers them; creating the Comment table fails (storage or auth down).
        var outage = new RequestFailedException(403, "This request is not authorized to perform this operation.");
        var commentTable = new Mock<TableClient>();
        commentTable.Setup(t => t.CreateIfNotExists(It.IsAny<CancellationToken>())).Throws(outage);
        var service = new Mock<TableServiceClient>();
        service.Setup(s => s.GetTableClient(It.IsAny<string>())).Returns(new Mock<TableClient>().Object);
        service.Setup(s => s.GetTableClient(TableNames.Comments)).Returns(commentTable.Object);
        var log = new ListLogger<ActivityNoteConversionStartup>();
        var services = new ServiceCollection();
        services.AddSingleton(service.Object);
        services.AddSingleton(sp => new TableStore<ActivityEntity>(sp.GetRequiredService<TableServiceClient>(), TableNames.Activities, TableNames.ActivityPartition));
        services.AddSingleton(sp => new TableStore<CommentEntity>(sp.GetRequiredService<TableServiceClient>(), TableNames.Comments, TableNames.CommentPartition));
        services.AddSingleton<ILogger<ActivityNoteConversion>>(new ListLogger<ActivityNoteConversion>());
        services.AddSingleton<ILogger<ActivityNoteConversionStartup>>(log);
        services.AddSingleton<ActivityNoteConversion>();
        services.AddHostedService<ActivityNoteConversionStartup>();
        await using var provider = services.BuildServiceProvider();

        // The host resolves every hosted service before it starts them: that must not throw.
        var startup = provider.GetServices<IHostedService>().OfType<ActivityNoteConversionStartup>().Single();
        await startup.StartAsync(default);
        // Awaiting the hosted service's task throws when the failure faulted it.
        await startup.ExecuteTask!;

        var entry = Assert.ContainsSingle(log.Entries);
        Assert.AreEqual(LogLevel.Error, entry.Level);
        Assert.AreSame(outage, entry.Exception);
        await startup.StopAsync(default);
    }
}
