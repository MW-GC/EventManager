namespace MW_GC.EventManager.Web.Shared.Events;

/// <summary>
/// The rules the Event metrics form checks before it sends anything. Each is the API's own rule (FieldRules.Count
/// and FieldRules.Money with EventMetricsValidator's limits, the same messages), so the form refuses what the API
/// would refuse; a test compares them value for value and keeps the limits equal. They live here and not in the
/// Shared project because FieldRules and EventMetricsValidator belong to the API.
/// </summary>
public static class EventMetricsRules
{
    /// <summary>The API's EventMetricsValidator.MaxCount; a test keeps the two equal.</summary>
    public const int MaxCount = 1_000_000;

    /// <summary>The API's EventMetricsValidator.MaxAmount; a test keeps the two equal.</summary>
    public const decimal MaxAmount = 1_000_000_000m;

    /// <summary>The message for a count that is not a whole number from 0 to <see cref="MaxCount"/>.</summary>
    public static string CountMessage(string field) => $"{field} must be a whole number from 0 to {MaxCount}.";

    /// <summary>The message for an amount outside 0 to <see cref="MaxAmount"/> or with more than 2 decimal places.</summary>
    public static string AmountMessage(string field) => $"{field} must be between 0 and {MaxAmount} with at most 2 decimal places.";

    /// <summary>FieldRules.Count: null, or a whole number from 0 to <see cref="MaxCount"/>. Otherwise the message.</summary>
    public static string? Count(int? value, string field) =>
        value is < 0 || value > MaxCount ? CountMessage(field) : null;

    /// <summary>FieldRules.Money: null, or 0 to <see cref="MaxAmount"/> with at most 2 decimal places. Otherwise the message.</summary>
    public static string? Money(decimal? value, string field) =>
        value is { } amount && (amount < 0 || amount > MaxAmount || decimal.Round(amount, 2) != amount) ? AmountMessage(field) : null;
}
