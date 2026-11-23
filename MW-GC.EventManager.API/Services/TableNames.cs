namespace MW_GC.EventManager.API.Services;

/// <summary>
/// The table and partition name of each of the seven <see cref="TableStore{TEntity}"/> tables.
/// <see cref="TableStoreRegistration.AddTableStores"/> registers the stores with them, and the
/// old-text conversion (<see cref="ActivityNoteConversion"/>) reads the Activity rows raw and writes
/// its marker beside the Comments with them. Renaming one moves stored data out of reach, so a test
/// pins every value as a literal.
/// </summary>
internal static class TableNames
{
    public const string Games = "Games";
    public const string GamePartition = "Game";
    public const string Activities = "Activities";
    public const string ActivityPartition = "Activity";
    public const string Events = "Events";
    public const string EventPartition = "Event";
    public const string Themes = "Themes";
    public const string ThemePartition = "Theme";
    public const string Holidays = "Holidays";
    public const string HolidayPartition = "Holiday";
    public const string EventMetrics = "EventMetrics";
    public const string EventMetricsPartition = "Metrics";
    public const string Comments = "ActivityComments";
    public const string CommentPartition = "Comment";
}
