using System.Text.Json;
using System.Text.Json.Serialization;

namespace MW_GC.EventManager.Shared.Series;

/// <summary>
/// Writes a rule's enum (<see cref="RecurrenceKind"/>, <see cref="WeekOfMonth"/>, <see cref="HolidayPreset"/>,
/// the weekday) to JSON by name, so a stored Series row keeps meaning the same thing when a member is added
/// in the middle later. Reads one member's name in any case, or a JSON number. Anything else in a string (a
/// name that is not a member, a comma list such as <c>"Weekly,BiWeekly"</c>, which <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/>
/// would OR into another member, or a number written as a string) reads as an undefined value instead of
/// failing the whole body, so <see cref="RecurrenceRule.Validate"/> names the field (<c>Holiday preset must be
/// a known preset.</c>) instead of the generic malformed-body message.
/// </summary>
public sealed class NamedEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    /// <summary>What a name that is not a member reads as: no enum here defines it.</summary>
    private static readonly TEnum Unknown = (TEnum)Enum.ToObject(typeof(TEnum), int.MinValue);

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number when reader.TryGetInt32(out var number) => (TEnum)Enum.ToObject(typeof(TEnum), number),
            JsonTokenType.String => Named(reader.GetString()),
            _ => throw new JsonException($"{typeof(TEnum).Name} must be a name or a number."),
        };

    // One member's name, in any case: it starts with a letter (a number in a string does not) and holds no
    // comma (a comma list would be ORed into another member).
    private static TEnum Named(string? text) =>
        text?.Trim() is { Length: > 0 } name && char.IsLetter(name[0]) && !name.Contains(',')
        && Enum.TryParse<TEnum>(name, ignoreCase: true, out var value) && Enum.IsDefined(value)
            ? value
            : Unknown;

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        if (Enum.IsDefined(value)) writer.WriteStringValue(value.ToString());
        else writer.WriteNumberValue(Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture));
    }
}

/// <summary><see cref="NamedEnumConverter{TEnum}"/> for an optional value, for an enum this project does not own (the weekday).</summary>
public sealed class NullableNamedEnumConverter<TEnum> : JsonConverter<TEnum?> where TEnum : struct, Enum
{
    private static readonly NamedEnumConverter<TEnum> Inner = new();

    public override TEnum? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : Inner.Read(ref reader, typeof(TEnum), options);

    public override void Write(Utf8JsonWriter writer, TEnum? value, JsonSerializerOptions options)
    {
        if (value is { } known) Inner.Write(writer, known, options);
        else writer.WriteNullValue();
    }
}
