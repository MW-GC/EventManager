using System.Security.Cryptography;
using System.Text;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;

namespace MW_GC.EventManager.API.Services;

/// <summary>
/// Translates the legacy export (<see cref="LegacyImport"/>) into current entities.
/// Legacy records use arbitrary string ids (e.g. "game-4"); these are converted to
/// stable Guids via <see cref="ToGuid"/> so that every foreign key — game→activity,
/// theme/holiday references, the winner reference, and the game/activity snapshots embedded in
/// event selections — stays linked after import. Games, activities and events are keyed
/// by their legacy id; themes and holidays are keyed by name because that is what legacy
/// activities reference them by.
/// </summary>
internal static class LegacyImportMapper
{
    // Fixed namespace so a given legacy id always maps to the same Guid across runs.
    private static readonly byte[] Namespace =
        new Guid("a1f3c9d2-2b7e-4c5a-9e6f-0d8b1c2e3f40").ToByteArray();

    public static ImportResult Map(LegacyImport import)
    {
        ArgumentNullException.ThrowIfNull(import);

        return new ImportResult(
            import.Games.Select(MapGameEntity).ToList(),
            import.Activities.Select(MapActivityEntity).ToList(),
            import.Events.Select(MapEvent).ToList(),
            import.Themes.Select(MapTheme).ToList(),
            import.Holidays.Select(MapHoliday).ToList());
    }

    private static GameEntity MapGameEntity(LegacyGame g) => new()
    {
        Id = ToGuid(g.Id),
        Name = g.Name,
        ImageUrl = g.ImageUrl,
        Website = g.Website,
        IconUrl = g.IconUrl
    };

    private static ActivityEntity MapActivityEntity(LegacyActivity a) => new()
    {
        Id = ToGuid(a.Id),
        GameId = ToGuid(a.GameId),
        Name = a.Name,
        Description = a.Description,
        Rules = a.Rules,
        // Legacy activities reference themes/holidays by name, so the ids are derived from
        // the same names the theme/holiday records are keyed on (see MapTheme/MapHoliday).
        ThemeIds = a.Themes.Select(ToGuid).ToList(),
        HolidayIds = a.Holidays.Select(ToGuid).ToList(),
        SetupRequirements = a.SetupRequirements,
        Comments = string.IsNullOrEmpty(a.Comments) ? null : a.Comments
    };

    // Themes/holidays are keyed by name (not their legacy id) because activities reference
    // them by name; hashing the name on both sides keeps the link intact after import.
    private static ThemeEntity MapTheme(LegacyTheme t) => new()
    {
        Id = ToGuid(t.Name),
        Name = t.Name
    };

    private static HolidayEntity MapHoliday(LegacyHoliday h) => new()
    {
        Id = ToGuid(h.Name),
        Name = h.Name
    };

    private static EventEntity MapEvent(LegacyEvent e)
    {
        var selections = e.Selections
            .Where(s => s.Game is not null && s.Activity is not null)
            .Select(s => new Selection
            {
                Game = MapGameModel(s.Game!),
                Activity = MapActivityModel(s.Activity!)
            })
            .ToList();

        return new EventEntity
        {
            Id = ToGuid(e.Id),
            Name = e.Name,
            Date = e.Date,
            UniqueGamesOnly = e.UniqueGamesOnly,
            // Legacy records the winner as a game id; map it to the winning selection's
            // activity id so the winner keeps its full game+activity context after import.
            WinnerActivityId = ResolveWinnerActivityId(e.WinnerId, selections),
            Selections = selections
        };
    }

    // Legacy winner is a game id. Find the matching selection and return its activity id,
    // which is how winners are now identified. Returns null when no winner is set or matched.
    private static Guid? ResolveWinnerActivityId(string? legacyWinnerGameId, List<Selection> selections)
    {
        if (string.IsNullOrEmpty(legacyWinnerGameId))
            return null;

        var winnerGameId = ToGuid(legacyWinnerGameId);
        return selections.FirstOrDefault(s => s.Game.Id == winnerGameId)?.Activity.Id;
    }

    private static Game MapGameModel(LegacyGame g) => new()
    {
        Id = ToGuid(g.Id),
        Name = g.Name,
        ImageUrl = g.ImageUrl,
        Website = g.Website,
        IconUrl = g.IconUrl
    };

    private static Activity MapActivityModel(LegacyActivity a) => new()
    {
        Id = ToGuid(a.Id),
        GameId = ToGuid(a.GameId),
        Name = a.Name,
        Description = a.Description,
        Rules = a.Rules,
        ThemeIds = a.Themes.Select(ToGuid).ToList(),
        HolidayIds = a.Holidays.Select(ToGuid).ToList(),
        SetupRequirements = a.SetupRequirements,
        Comments = string.IsNullOrEmpty(a.Comments) ? null : a.Comments
    };

    /// <summary>
    /// Maps a legacy string id to a deterministic Guid: SHA-256 over a fixed namespace plus
    /// the id, with the version/variant nibbles stamped to produce a well-formed (name-based)
    /// Guid. The same input always yields the same Guid, which is what keeps imported
    /// relationships intact. Returns <see cref="Guid.Empty"/> when no id is present.
    /// SHA-256 is used purely for stable hashing here, not for security.
    /// </summary>
    private static Guid ToGuid(string? legacyId)
    {
        if (string.IsNullOrWhiteSpace(legacyId))
            return Guid.Empty;

        var nameBytes = Encoding.UTF8.GetBytes(legacyId);
        var buffer = new byte[Namespace.Length + nameBytes.Length];
        Buffer.BlockCopy(Namespace, 0, buffer, 0, Namespace.Length);
        Buffer.BlockCopy(nameBytes, 0, buffer, Namespace.Length, nameBytes.Length);

        var hash = SHA256.HashData(buffer);
        var guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, 16);

        // Stamp a name-based version (5) and the RFC 4122 variant so the value is well-formed.
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);

        return new Guid(guidBytes);
    }
}
