namespace MW_GC.EventManager.API.Services;

/// <summary>
/// The table and partition names that more than one place must agree on: <c>Program.cs</c>
/// registers the stores with them, and the old-text conversion (<see cref="ActivityNoteConversion"/>)
/// reads the Activity rows raw and writes its marker beside the Comments with them.
/// </summary>
internal static class TableNames
{
    public const string Activities = "Activities";
    public const string ActivityPartition = "Activity";
    public const string Comments = "ActivityComments";
    public const string CommentPartition = "Comment";
}
