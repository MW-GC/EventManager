using System.Diagnostics.CodeAnalysis;

namespace MW_GC.EventManager.Web.Shared.Library;

/// <summary>
/// The rules a Library item's editor checks before it sends anything. Each is the API's own rule (FieldRules,
/// with the same limit and messages), so the form refuses what the API would refuse; tests keep them equal.
/// Each <c>...Message</c> returns null when the value passes, otherwise the message shown under the field.
/// </summary>
public static class LibraryRules
{
    /// <summary>The API's FieldRules.NameMaxLength; a test keeps the two equal.</summary>
    public const int NameMaxLength = 100;

    /// <summary>The API's FieldRules.UrlMaxLength, which a Website is held to; a test keeps the two equal.</summary>
    public const int WebsiteMaxLength = 2048;

    /// <summary>Null for a valid name; otherwise the message. The name counts after trimming.</summary>
    public static string? NameMessage(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return "Name is required.";
        return trimmed.Length > NameMaxLength ? $"Name must be {NameMaxLength} characters or fewer." : null;
    }

    /// <summary>
    /// The API's Website rules, in the API's order: at most <see cref="WebsiteMaxLength"/> characters, then
    /// (FieldRules.HttpAddress) empty or whitespace, or an absolute http or https address (any case). Null when
    /// it passes; otherwise the message. The http rule is also what makes a stored Website a link.
    /// </summary>
    public static string? WebsiteMessage(string? value) =>
        value is { Length: > WebsiteMaxLength } ? $"Website must be {WebsiteMaxLength} characters or fewer."
        : string.IsNullOrWhiteSpace(value) || IsHttpAddress(value) ? null
        : "Website must be an http or https address.";

    /// <summary>The message under an Activity's Game field when none is chosen.</summary>
    public const string GameRequired = "Game is required.";

    /// <summary>
    /// An Activity's Game: null once one is chosen, otherwise <see cref="GameRequired"/>. The API refuses the same
    /// case with its own words (ActivityValidator: "GameId is required."); the form names the field as the user sees it.
    /// </summary>
    public static string? GameMessage(Guid gameId) => gameId == Guid.Empty ? GameRequired : null;

    /// <summary>An absolute http or https address, so safe to use as a link (never javascript: or similar).</summary>
    public static bool IsHttpAddress([NotNullWhen(true)] string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

/// <summary>
/// One failed field rule from a page's <see cref="LibraryPage{TItem}.Rules"/>: the item's property name, such as
/// nameof(GameEntity.Website), and the message shown under that field by a <c>LibraryFieldMessage</c>.
/// </summary>
public sealed record LibraryFieldError(string Field, string Message);
