using System.Globalization;
using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Web.Shared.Events;

/// <summary>
/// One field of the Event metrics form: its label and placeholder, the text typed into it, its message, and
/// how that text is read, checked with the API's rule and written to its metric on <see cref="EventMetricsEntity"/>.
/// The form keeps one list of these (EventMetricsForm.NewFields), so adding a metric is one change on the
/// entity, one rule in the API's EventMetricsValidator and one entry in that list.
/// </summary>
internal abstract class EventMetricsField(string label, string placeholder)
{
    public string Label { get; } = label;
    public string Placeholder { get; } = placeholder;
    /// <summary>The text field's id, which its label points at.</summary>
    public string Id => "metrics-" + Label.ToLowerInvariant().Replace(' ', '-');

    /// <summary>The text as typed.</summary>
    public string Text { get; set; } = string.Empty;
    /// <summary>The message shown under the field, or null.</summary>
    public string? Error { get; set; }

    /// <summary>A whole-number metric (FieldRules.Count on the API).</summary>
    public static EventMetricsField Count(string label, Func<EventMetricsEntity, int?> get, Action<EventMetricsEntity, int?> set) =>
        new Typed<int>(label, "e.g. 12", get, set,
            (string text, out int value) => int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value),
            v => v.ToString(CultureInfo.InvariantCulture), v => EventMetricsRules.Count(v, label), EventMetricsRules.CountMessage(label));

    /// <summary>An amount of money (FieldRules.Money on the API).</summary>
    public static EventMetricsField Amount(string label, Func<EventMetricsEntity, decimal?> get, Action<EventMetricsEntity, decimal?> set) =>
        new Typed<decimal>(label, "e.g. 40.50", get, set,
            (string text, out decimal value) => decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value),
            v => v.ToString(CultureInfo.InvariantCulture), v => EventMetricsRules.Money(v, label), EventMetricsRules.AmountMessage(label));

    /// <summary>Shows the stored value; clears the message.</summary>
    public abstract void Show(EventMetricsEntity stored);

    /// <summary>
    /// Whether the typed text is a change from the stored value, compared as values: <c>40.50</c> is the stored
    /// <c>40.5</c>, and an empty field is a stored null. Text that cannot be read is a change.
    /// </summary>
    public abstract bool Differs(EventMetricsEntity stored);

    /// <summary>
    /// Reads the typed text (empty is null, a cleared metric), checks it with the API's rule and writes it to
    /// <paramref name="target"/>. Sets <see cref="Error"/> and returns false when the text is refused.
    /// </summary>
    public abstract bool Write(EventMetricsEntity target);

    private delegate bool Parser<T>(string text, out T value);

    private sealed class Typed<T>(
        string label, string placeholder,
        Func<EventMetricsEntity, T?> get, Action<EventMetricsEntity, T?> set,
        Parser<T> parse, Func<T, string> format, Func<T?, string?> rule, string unreadable)
        : EventMetricsField(label, placeholder) where T : struct
    {
        public override void Show(EventMetricsEntity stored)
        {
            Text = get(stored) is { } value ? format(value) : string.Empty;
            Error = null;
        }

        public override bool Differs(EventMetricsEntity stored) => !TryRead(out var value) || !Nullable.Equals(value, get(stored));

        public override bool Write(EventMetricsEntity target)
        {
            Error = TryRead(out var value) ? rule(value) : unreadable;
            if (Error is not null) return false;
            set(target, value);
            return true;
        }

        private bool TryRead(out T? value)
        {
            var text = Text.Trim();
            value = null;
            if (text.Length == 0) return true;
            if (!parse(text, out var parsed)) return false;
            value = parsed;
            return true;
        }
    }
}
