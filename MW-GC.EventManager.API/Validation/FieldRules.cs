namespace MW_GC.EventManager.API.Validation;

/// <summary>Field-level rules shared by the entity validators, so the limits live in one place.</summary>
internal static class FieldRules
{
    public const int NameMaxLength = 100;
    public const int TextMaxLength = 2000;
    public const int UrlMaxLength = 2048;

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
}
