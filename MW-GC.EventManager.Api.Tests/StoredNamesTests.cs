using MW_GC.EventManager.API.Services;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// The table and partition names stored data lives under, written out as literals once. Renaming
/// any of them moves the stored rows out of the app's reach, so these are never read from the
/// production constants. Other tests may use the constants, because <see cref="StoredNamesTests"/>
/// fails on a rename.
/// </summary>
internal static class StoredNames
{
    /// <summary>The eight tables behind a <see cref="TableStore{TEntity}"/>, each with its one partition.</summary>
    public static readonly (string Table, string Partition)[] Stores =
    [
        ("Games", "Game"),
        ("Activities", "Activity"),
        ("Events", "Event"),
        ("Themes", "Theme"),
        ("Holidays", "Holiday"),
        ("EventMetrics", "Metrics"),
        ("ActivityComments", "Comment"),
        ("Series", "Series"),
    ];

    /// <summary>Every table the API creates: the eight above and the two feedback tables.</summary>
    public static readonly string[] AllTables =
    [
        "Activities", "ActivityComments", "EventMetrics", "Events", "FeedbackLikes", "FeedbackLinks", "Games", "Holidays", "Series", "Themes",
    ];
}

[TestClass]
public class StoredNamesTests
{
    [TestMethod]
    public void TheTableAndPartitionNamesOfStoredDataAreUnchanged()
    {
        var actual = new (string What, string Expected, string Actual)[]
        {
            ("Games table", "Games", TableNames.Games),
            ("Game partition", "Game", TableNames.GamePartition),
            ("Activities table", "Activities", TableNames.Activities),
            ("Activity partition", "Activity", TableNames.ActivityPartition),
            ("Events table", "Events", TableNames.Events),
            ("Event partition", "Event", TableNames.EventPartition),
            ("Themes table", "Themes", TableNames.Themes),
            ("Theme partition", "Theme", TableNames.ThemePartition),
            ("Holidays table", "Holidays", TableNames.Holidays),
            ("Holiday partition", "Holiday", TableNames.HolidayPartition),
            ("Event metrics table", "EventMetrics", TableNames.EventMetrics),
            ("Event metrics partition", "Metrics", TableNames.EventMetricsPartition),
            ("Comments table", "ActivityComments", TableNames.Comments),
            ("Comment partition", "Comment", TableNames.CommentPartition),
            ("Series table", "Series", TableNames.Series),
            ("Series partition", "Series", TableNames.SeriesPartition),
            ("old-text conversion marker partition", "Migration", ActivityNoteConversion.MarkerPartitionKey),
            ("feedback links table", "FeedbackLinks", FeedbackStore.LinksTable),
            ("feedback link Event partition", "Event", FeedbackStore.EventPartition),
            ("feedback link token partition", "Token", FeedbackStore.TokenPartition),
            ("feedback likes table", "FeedbackLikes", FeedbackStore.LikesTable),
        };

        // Every mismatch in one message, so a rename names everything it moved.
        var renamed = actual.Where(n => n.Expected != n.Actual).Select(n => $"{n.What}: \"{n.Actual}\", stored data is under \"{n.Expected}\"");
        Assert.AreEqual("", string.Join("; ", renamed));
    }

    [TestMethod]
    public void TheLiteralListsTheOtherTestsUseMatchTheProductionNames()
    {
        // Each literal list against the production values, so a list that drifts from the app fails here.
        CollectionAssert.AreEqual(StoredNames.Stores, new[]
        {
            (TableNames.Games, TableNames.GamePartition),
            (TableNames.Activities, TableNames.ActivityPartition),
            (TableNames.Events, TableNames.EventPartition),
            (TableNames.Themes, TableNames.ThemePartition),
            (TableNames.Holidays, TableNames.HolidayPartition),
            (TableNames.EventMetrics, TableNames.EventMetricsPartition),
            (TableNames.Comments, TableNames.CommentPartition),
            (TableNames.Series, TableNames.SeriesPartition),
        }, "StoredNames.Stores");
        CollectionAssert.AreEquivalent(StoredNames.AllTables, new[]
        {
            TableNames.Games, TableNames.Activities, TableNames.Events, TableNames.Themes, TableNames.Holidays,
            TableNames.EventMetrics, TableNames.Comments, TableNames.Series, FeedbackStore.LinksTable, FeedbackStore.LikesTable,
        }, "StoredNames.AllTables");
    }
}
