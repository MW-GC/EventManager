using System.Globalization;

namespace MW_GC.EventManager.Web.Services;

/// <summary>
/// How Activity performance reads (#136, #137), shared by the Stats page and the Activity details dialog, so the
/// same number shows the same text in both places. A number with no value shows <see cref="PlayStats.NoValue"/>
/// through the <c>PerformanceValue</c> component.
/// </summary>
public static class PerformanceFormat
{
    /// <summary>The caveat shown with Activity performance on the Stats page and in the Activity details.</summary>
    public const string Caveat =
        "Performance cannot tell a weak Activity from a weak night: an Event has several Selections, so its numbers belong to all of them.";

    /// <summary>A whole percent ("50%"), the same in every culture; null when there is no rate.</summary>
    public static string? Percent(decimal? rate) =>
        rate is { } r ? (r * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : null;

    /// <summary>One decimal ("1.0"), the same in every culture; null when there is no value.</summary>
    public static string? OneDecimal(decimal? value) => value?.ToString("0.0", CultureInfo.InvariantCulture);
}
