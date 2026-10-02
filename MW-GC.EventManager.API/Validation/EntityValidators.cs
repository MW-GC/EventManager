using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.API.Validation;

// One validator per entity, called by BOTH the create and the update route so the rules
// cannot drift. Each one normalises the entity in place (trimmed names, non-null id lists)
// and returns the first error, naming the field, or null when the entity may be stored.

internal static class GameValidator
{
    public static string? Validate(GameEntity game)
    {
        var error = FieldRules.Name(game.Name, out var name);
        game.Name = name;
        return error
            ?? FieldRules.MaxLength(game.Website, nameof(GameEntity.Website), FieldRules.UrlMaxLength)
            ?? FieldRules.MaxLength(game.ImageUrl, nameof(GameEntity.ImageUrl), FieldRules.UrlMaxLength)
            ?? FieldRules.MaxLength(game.IconUrl, nameof(GameEntity.IconUrl), FieldRules.UrlMaxLength);
    }
}

internal static class ThemeValidator
{
    public static string? Validate(ThemeEntity theme)
    {
        var error = FieldRules.Name(theme.Name, out var name);
        theme.Name = name;
        return error;
    }
}

internal static class HolidayValidator
{
    public static string? Validate(HolidayEntity holiday)
    {
        var error = FieldRules.Name(holiday.Name, out var name);
        holiday.Name = name;
        return error;
    }
}

internal static class ActivityValidator
{
    public const string GameNotFoundMessage = "Game not found.";

    /// <summary>
    /// Checks the fields that need no storage lookup. The caller still checks that
    /// <see cref="ActivityEntity.GameId"/> names an existing Game.
    /// </summary>
    public static string? Validate(ActivityEntity activity)
    {
        // Lenient: a missing or null id list is stored as an empty, de-duplicated list.
        activity.ThemeIds = activity.ThemeIds?.Distinct().ToList() ?? [];
        activity.HolidayIds = activity.HolidayIds?.Distinct().ToList() ?? [];

        var error = FieldRules.Name(activity.Name, out var name);
        activity.Name = name;
        return error
            ?? (activity.GameId == Guid.Empty ? $"{nameof(ActivityEntity.GameId)} is required." : null)
            ?? FieldRules.MaxLength(activity.Description, nameof(ActivityEntity.Description), FieldRules.TextMaxLength)
            ?? FieldRules.MaxLength(activity.Rules, nameof(ActivityEntity.Rules), FieldRules.TextMaxLength)
            ?? FieldRules.MaxLength(activity.SetupRequirements, nameof(ActivityEntity.SetupRequirements), FieldRules.TextMaxLength)
            ?? ValidateComments(activity.Comments);
    }

    /// <summary>The Comments rule, shared with the PATCH comments route.</summary>
    public static string? ValidateComments(string? comments) =>
        FieldRules.MaxLength(comments, nameof(ActivityEntity.Comments), FieldRules.TextMaxLength);
}

internal static class EventValidator
{
    /// <summary>The Event name rule, then the existing selection rules (messages unchanged).</summary>
    public static string? Validate(EventEntity evt)
    {
        var error = FieldRules.Name(evt.Name, out var name);
        evt.Name = name;
        return error ?? evt.ValidateSelections();
    }
}
