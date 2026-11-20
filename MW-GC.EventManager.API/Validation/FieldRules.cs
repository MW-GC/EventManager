namespace MW_GC.EventManager.API.Validation;

/// <summary>Field-level rules shared by the entity validators, so the limits live in one place.</summary>
internal static class FieldRules
{
    public const int NameMaxLength = 100;
    public const int TextMaxLength = 2000;
    public const int UrlMaxLength = 2048;
    public const int CommentTextMaxLength = 1000;

    /// <summary>
    /// Trims a name and checks it is non-empty and within <see cref="NameMaxLength"/>.
    /// Returns an error naming the field, or <c>null</c> when the trimmed value is valid.
    /// </summary>
    public static string? Name(string? value, out string trimmed, string field = "Name")
    {
        trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return $"{field} is required.";
        return MaxLength(trimmed, field, NameMaxLength);
    }

    /// <summary>Returns an error naming the field when the value is longer than <paramref name="max"/>.</summary>
    public static string? MaxLength(string? value, string field, int max) =>
        value is not null && value.Length > max ? $"{field} must be {max} characters or fewer." : null;

    /// <summary>
    /// An optional count: null, or a whole number from 0 to <paramref name="max"/>. A fractional
    /// JSON number never gets here; it fails to read into an <c>int?</c> and is a malformed body.
    /// </summary>
    public static string? Count(int? value, string field, int max) =>
        value is < 0 || value > max ? $"{field} must be a whole number from 0 to {max}." : null;

    /// <summary>An optional amount of money: null, or 0 to <paramref name="max"/> with at most 2 decimal places.</summary>
    public static string? Money(decimal? value, string field, decimal max) =>
        value is { } amount && (amount < 0 || amount > max || decimal.Round(amount, 2) != amount)
            ? $"{field} must be between 0 and {max} with at most 2 decimal places."
            : null;
}
