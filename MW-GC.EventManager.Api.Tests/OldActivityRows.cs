using Azure.Data.Tables;
using MW_GC.EventManager.API.Services;

namespace MW_GC.EventManager.Api.Tests;

/// <summary>
/// Activity rows as a version before #133 stored them, with the old single text in a raw
/// <c>Comments</c> column, for the tests that check the API never writes that column back.
/// </summary>
internal static class OldActivityRows
{
    /// <summary>The old stored column that held the Activity's single text.</summary>
    public const string OldTextColumn = "Comments";

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
