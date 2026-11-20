namespace MW_GC.EventManager.Web.Shared.Library;

/// <summary>
/// The rules a Library item's editor checks before it sends anything. Each is the API's own rule (FieldRules,
/// with the same limit and messages), so the form refuses what the API would refuse; tests keep them equal.
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

    /// <summary>
    /// The API's FieldRules.HttpAddress for a Website: null when it is empty or whitespace, or an absolute http or
    /// https address (any case); otherwise the message. It is also what makes a stored Website a link.
    /// </summary>
    public static string? Website(string? value) =>
        string.IsNullOrWhiteSpace(value) || IsHttpAddress(value) ? null : "Website must be an http or https address.";

    /// <summary>An absolute http or https address, so safe to use as a link (never javascript: or similar).</summary>
    public static bool IsHttpAddress(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

/// <summary>
/// One failed field rule from a page's <see cref="LibraryPage{TItem}.Rules"/>: the item's property name, such as
/// nameof(GameEntity.Website), and the message shown under that field by a <c>LibraryFieldMessage</c>.
/// </summary>
public sealed record LibraryFieldError(string Field, string Message);
