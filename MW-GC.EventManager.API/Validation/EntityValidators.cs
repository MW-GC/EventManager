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
            ?? FieldRules.HttpAddress(game.Website, nameof(GameEntity.Website))
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
            ?? FieldRules.MaxLength(activity.SetupRequirements, nameof(ActivityEntity.SetupRequirements), FieldRules.TextMaxLength);
    }
}

internal static class CommentValidator
{
    public const string TextRequiredMessage = "Comment text is required.";
    public const string EventNotFoundMessage = "Event not found.";

    /// <summary>
    /// The Comment text rule, shared by add and edit: trimmed, non-empty and at most
    /// <see cref="FieldRules.CommentTextMaxLength"/> characters. The caller checks that the
    /// Activity and any Event exist.
    /// </summary>
    public static string? ValidateText(string? text, out string trimmed)
    {
        trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return TextRequiredMessage;
        return FieldRules.MaxLength(trimmed, "Comment text", FieldRules.CommentTextMaxLength);
    }
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

internal static class SeriesValidator
{
    public const string StartRequiredMessage = "Start is required.";
    public const string RuleRequiredMessage = "Repeat must be a known rule.";
    public const string EndMessage = "End must be after the start.";
    public const int MaxSlots = EventEntity.MaximumSelections;
    public static readonly string SlotCountMessage = $"Slot count must be from 1 to {MaxSlots}.";

    /// <summary>
    /// A Series' name, start, rule, end and slot count, in that order; the first failure wins. The Filter
    /// id lists are lenient, as an Activity's are: a missing list is stored empty and duplicates are removed.
    /// The cancelled occurrence keys belong to the API and are not checked here.
    /// </summary>
    public static string? Validate(SeriesEntity series)
    {
        var error = FieldRules.Name(series.Name, out var name);
        series.Name = name;
        series.Filters ??= new SeriesFilters();
        series.Filters.GameIds = series.Filters.GameIds?.Distinct().ToList() ?? [];
        series.Filters.ThemeIds = series.Filters.ThemeIds?.Distinct().ToList() ?? [];
        series.Filters.HolidayIds = series.Filters.HolidayIds?.Distinct().ToList() ?? [];
        if (error is not null) return error;

        var schedule = series.Schedule;
        if (schedule is null || schedule.First == default) return StartRequiredMessage;
        if (schedule.Rule is null) return RuleRequiredMessage;
        if (schedule.Rule.Validate() is { } rule) return rule;
        if (schedule.End is { } end && end <= schedule.First) return EndMessage;
        if (series.SlotCount is < 1 or > MaxSlots) return SlotCountMessage;
        return null;
    }
}

internal static class EventMetricsValidator
{
    public const int MaxCount = 1_000_000;
    public const decimal MaxAmount = 1_000_000_000m;

    /// <summary>
    /// One rule per metric, in field order; the first failure wins. Every field is optional and
    /// there is no cross-check between attendees, new and returning (decided in #123).
    /// </summary>
    public static string? Validate(EventMetricsEntity metrics) =>
        FieldRules.Count(metrics.Attendees, "Attendees", MaxCount)
        ?? FieldRules.Count(metrics.NewAttendees, "New attendees", MaxCount)
        ?? FieldRules.Count(metrics.ReturningAttendees, "Returning attendees", MaxCount)
        ?? FieldRules.Money(metrics.AmountSpent, "Amount spent", MaxAmount);
}
