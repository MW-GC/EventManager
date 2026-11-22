using System.Globalization;
using MW_GC.EventManager.Shared.Series;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// Literal in, literal out for the Occurrence generator tests: instants are written as
/// <c>2027-03-18T19:00:00-05:00</c> and the generator's answer is rendered the same way, so an assertion
/// checks the wall clock and the offset, not only the instant.
/// </summary>
internal static class SeriesTestSupport
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:sszzz";

    public static DateTimeOffset At(string literal) =>
        DateTimeOffset.ParseExact(literal, Format, CultureInfo.InvariantCulture);

    public static string[] Render(IEnumerable<DateTimeOffset> instants) =>
        instants.Select(i => i.ToString(Format, CultureInfo.InvariantCulture)).ToArray();

    public static string[] Generate(
        RecurrenceRule rule, string first, string now, string[]? existing = null, string[]? skipped = null) =>
        Render(OccurrenceGenerator.Generate(
            new RecurrenceSeries(At(first), rule), At(now),
            (existing ?? []).Select(At).ToArray(), (skipped ?? []).Select(At).ToArray()));

    public static void AreEqual(string[] expected, string[] actual) =>
        CollectionAssert.AreEqual(expected, actual,
            $"expected [{string.Join(", ", expected)}] but got [{string.Join(", ", actual)}]");
}
