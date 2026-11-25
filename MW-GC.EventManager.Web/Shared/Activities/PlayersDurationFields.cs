using System.Globalization;
using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Shared.Library;

namespace MW_GC.EventManager.Web.Shared.Activities;

/// <summary>
/// The players and duration fields of the Activities editor (#98) for one item in the form: the text typed into
/// each, read as the API reads it (empty is not set, so clearing a field unsets it), and the API's own rules and
/// messages (ActivityValidator), checked before anything is sent. A test compares these rules with the API's.
/// </summary>
public sealed class PlayersDurationFields
{
    /// <summary>The API's ActivityValidator.MaxPlayerCount.</summary>
    public const int MaxPlayerCount = 99;

    /// <summary>The API's ActivityValidator.MaxDurationMinutes.</summary>
    public const int MaxDurationMinutes = 1440;

    /// <summary>The API's ActivityValidator.MinAboveMaxMessage.</summary>
    public const string MinAboveMaxMessage = "Minimum players cannot be more than maximum players.";

    /// <summary>One field: its key (the item's property), its label, which is also its accessible name, and its limit.</summary>
    public sealed record Field(LibraryField Key, string Label, string Placeholder, int Max,
        Func<ActivityEntity, int?> Get, Action<ActivityEntity, int?> Set)
    {
        /// <summary>The message for a value that is not a whole number from 1 to <see cref="Max"/>.</summary>
        public string Message => $"{Label} must be a whole number from 1 to {Max}.";

        /// <summary>The text field's id, which its label points at.</summary>
        public string Id => $"activity-{Key.Property}";
    }

    public static readonly Field MinPlayers = new(new(nameof(ActivityEntity.MinPlayers)), "Minimum players", "e.g. 2",
        MaxPlayerCount, a => a.MinPlayers, (a, v) => a.MinPlayers = v);

    public static readonly Field MaxPlayers = new(new(nameof(ActivityEntity.MaxPlayers)), "Maximum players", "e.g. 6",
        MaxPlayerCount, a => a.MaxPlayers, (a, v) => a.MaxPlayers = v);

    public static readonly Field Duration = new(new(nameof(ActivityEntity.DurationMinutes)), "Duration in minutes", "e.g. 45",
        MaxDurationMinutes, a => a.DurationMinutes, (a, v) => a.DurationMinutes = v);

    /// <summary>The three fields, in the order the form shows them.</summary>
    public static IReadOnlyList<Field> All { get; } = [MinPlayers, MaxPlayers, Duration];

    private readonly Dictionary<Field, string> _texts = [];

    /// <summary>The fields of <paramref name="item"/>, the editor's copy, showing its stored values.</summary>
    public PlayersDurationFields(ActivityEntity item)
    {
        Item = item;
        foreach (var field in All)
            _texts[field] = field.Get(item) is { } value ? value.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }

    /// <summary>The item in the form these fields write to.</summary>
    public ActivityEntity Item { get; }

    public string Text(Field field) => _texts[field];

    /// <summary>
    /// Keeps the typed text and, when it reads as a whole number or is empty, writes that value (or null) to the
    /// item. Text that does not read keeps the item's value; its rule refuses the save.
    /// </summary>
    public void Type(Field field, string? text)
    {
        _texts[field] = text ?? string.Empty;
        if (TryRead(_texts[field], out var value)) field.Set(Item, value);
    }

    /// <summary>The fields whose text is not a whole number, by label; null when every field reads.</summary>
    public string? Unreadable()
    {
        var unreadable = All.Where(f => !TryRead(_texts[f], out _)).Select(f => f.Label).ToList();
        return unreadable.Count == 0 ? null : string.Join(", ", unreadable);
    }

    /// <summary>
    /// The API's rules in the API's order: each field a whole number from 1 to its limit (text that does not read
    /// gets the same message), then the minimum at most the maximum, shown under the minimum.
    /// </summary>
    public IEnumerable<LibraryFieldError> Errors()
    {
        var failed = false;
        foreach (var field in All)
        {
            if (!TryRead(_texts[field], out var value) || value is < 1 || value > field.Max)
            {
                failed = true;
                yield return new LibraryFieldError(field.Key, field.Message);
            }
        }
        if (!failed && Item.MinPlayers > Item.MaxPlayers) yield return new LibraryFieldError(MinPlayers.Key, MinAboveMaxMessage);
    }

    private static bool TryRead(string text, out int? value)
    {
        value = null;
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return true;
        if (!int.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)) return false;
        value = number;
        return true;
    }
}
