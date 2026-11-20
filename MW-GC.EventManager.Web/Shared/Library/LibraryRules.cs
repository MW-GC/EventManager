namespace MW_GC.EventManager.Web.Shared.Library;

/// <summary>
/// The name rule every Library item's editor checks before it sends anything. It is the API's own rule
/// (FieldRules.Name, with the same limit and messages), so the form refuses what the API would refuse.
/// </summary>
public static class LibraryRules
{
    /// <summary>The API's FieldRules.NameMaxLength; a test keeps the two equal.</summary>
    public const int NameMaxLength = 100;

    /// <summary>Null for a valid name; otherwise the message. The name counts after trimming.</summary>
    public static string? Name(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return "Name is required.";
        return trimmed.Length > NameMaxLength ? $"Name must be {NameMaxLength} characters or fewer." : null;
    }
}
