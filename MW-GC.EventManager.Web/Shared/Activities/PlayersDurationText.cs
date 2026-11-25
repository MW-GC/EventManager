using System.Globalization;

namespace MW_GC.EventManager.Web.Shared.Activities;

/// <summary>
/// The one wording of an Activity's players and duration (#98), shared by everything that shows them (the Event
/// details dialog now; the announcement text later). Each returns null when there is nothing to show.
/// </summary>
public static class PlayersDurationText
{
    /// <summary>
    /// "2-6 players", "up to 6 players", "2 or more players", or "4 players" when the minimum is the maximum;
    /// null when neither is set. One player is "player".
    /// </summary>
    public static string? Players(int? minPlayers, int? maxPlayers) => (minPlayers, maxPlayers) switch
    {
        (null, null) => null,
        ({ } min, { } max) when min == max => Count(min),
        ({ } min, { } max) => $"{Number(min)}-{Count(max)}",
        (null, { } max) => $"up to {Count(max)}",
        ({ } min, null) => $"{Number(min)} or more players",
    };

    /// <summary>"45 min"; null when not set.</summary>
    public static string? Duration(int? minutes) => minutes is { } value ? $"{Number(value)} min" : null;

    private static string Count(int players) => players == 1 ? "1 player" : $"{Number(players)} players";

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
