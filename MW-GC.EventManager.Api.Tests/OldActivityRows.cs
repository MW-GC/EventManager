using Azure.Data.Tables;
using MW_GC.EventManager.API.Services;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// Activity rows as a version before #133 stored them, with the old single text in a raw
/// <c>Comments</c> column. Shared by the in-memory and the Azurite conversion tests.
/// </summary>
internal static class OldActivityRows
{
    /// <summary>The old stored column that held the Activity's single text.</summary>
    public const string OldTextColumn = "Comments";

    /// <summary>A fixed Activity id, for the tests that check the converted Comment's id.</summary>
    public static readonly Guid FixedActivityId = Guid.Parse("3f6d2c1e-8b4a-4d7e-9a15-6c2e0b7f4d91");

    /// <summary>
    /// The id the conversion must give <see cref="FixedActivityId"/>'s Comment: the first 16 bytes of
    /// SHA-256("activity-note:3f6d2c1e-8b4a-4d7e-9a15-6c2e0b7f4d91"), read as a Guid. Worked out once,
    /// outside the code under test, and written here as a literal.
    /// </summary>
    public static readonly Guid FixedActivityCommentId = Guid.Parse("7278f827-dd95-7658-52e4-601b19004731");

    /// <summary>
    /// An old Activity row. <paramref name="oldColumn"/> goes into the old <c>Comments</c> column as
    /// given (a text, or another type to test the skip); null leaves the column out.
    /// </summary>
    public static TableEntity Row(Guid id, Guid gameId, string name, object? oldColumn)
    {
        var row = new TableEntity(TableNames.ActivityPartition, id.ToString("D"))
        {
            ["GameId"] = gameId, ["Name"] = name, ["Description"] = "", ["Rules"] = "",
            ["ThemeIds"] = "[]", ["HolidayIds"] = "[]", ["SetupRequirements"] = "",
        };
        if (oldColumn is not null) row[OldTextColumn] = oldColumn;
        return row;
    }
}
