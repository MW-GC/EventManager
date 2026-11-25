using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.API.Services;

/// <summary>The registration of the eight <see cref="TableStore{TEntity}"/> tables, one singleton each.</summary>
internal static class TableStoreRegistration
{
    /// <summary>
    /// Registers one <see cref="TableStore{TEntity}"/> per table, under the names in <see cref="TableNames"/>,
    /// over the <see cref="TableServiceClient"/> the caller registers. A store creates its table
    /// the first time it is resolved.
    /// </summary>
    public static IServiceCollection AddTableStores(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
            new TableStore<GameEntity>(sp.GetRequiredService<TableServiceClient>(), TableNames.Games, TableNames.GamePartition));
        services.AddSingleton(sp =>
            new TableStore<ActivityEntity>(sp.GetRequiredService<TableServiceClient>(), TableNames.Activities, TableNames.ActivityPartition));
        services.AddSingleton(sp =>
            new TableStore<EventEntity>(sp.GetRequiredService<TableServiceClient>(), TableNames.Events, TableNames.EventPartition));
        services.AddSingleton(sp =>
            new TableStore<ThemeEntity>(sp.GetRequiredService<TableServiceClient>(), TableNames.Themes, TableNames.ThemePartition));
        services.AddSingleton(sp =>
            new TableStore<HolidayEntity>(sp.GetRequiredService<TableServiceClient>(), TableNames.Holidays, TableNames.HolidayPartition));
        // Event metrics (#134, ADR 0001): one row per Event, keyed by the Event id.
        services.AddSingleton(sp =>
            new TableStore<EventMetricsEntity>(sp.GetRequiredService<TableServiceClient>(), TableNames.EventMetrics, TableNames.EventMetricsPartition));
        // Comments (#129, ADR 0001): one row per Comment.
        services.AddSingleton(sp =>
            new TableStore<CommentEntity>(sp.GetRequiredService<TableServiceClient>(), TableNames.Comments, TableNames.CommentPartition));
        // Series (#204, ADR 0002): one row per Series; its Occurrences are rows in the Events table.
        services.AddSingleton(sp =>
            new TableStore<SeriesEntity>(sp.GetRequiredService<TableServiceClient>(), TableNames.Series, TableNames.SeriesPartition));
        return services;
    }
}
