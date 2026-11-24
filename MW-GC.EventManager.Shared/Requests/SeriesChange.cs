using MW_GC.EventManager.Shared.Entities;

namespace MW_GC.EventManager.Shared.Requests;

/// <summary>
/// The answer to a Series create, edit or cancel (#204): the Series as stored afterwards and how many of
/// its Occurrences were created, rewritten and deleted.
/// </summary>
public sealed class SeriesChange
{
    /// <summary>The Series at the request's address, as stored after the change.</summary>
    public SeriesEntity? Series { get; set; }

    /// <summary>A "this and following" edit only: the Series that starts at the chosen Occurrence.</summary>
    public SeriesEntity? NewSeries { get; set; }

    /// <summary>Occurrences generated and stored by this request.</summary>
    public int Created { get; set; }

    /// <summary>Existing Occurrences rewritten in place by this request.</summary>
    public int Updated { get; set; }

    /// <summary>Occurrences deleted by this request (moved ones included: they are made again under the new Series).</summary>
    public int Deleted { get; set; }
}
