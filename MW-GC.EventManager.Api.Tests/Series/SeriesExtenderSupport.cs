using System.Globalization;
using System.Linq.Expressions;
using Azure;
using Azure.Data.Tables;
using Moq;
using MW_GC.EventManager.API.Services;

namespace MW_GC.EventManager.Api.Tests.Series;

/// <summary>
/// The daily Series extension (#204) over a <see cref="SeriesHarness"/>'s tables, and what the tests compare:
/// stored rows written out whole, and how many writes reached a table.
/// </summary>
internal static class SeriesExtenderSupport
{
    /// <summary>A run over the harness' Series and Event tables, logging into <paramref name="log"/>.</summary>
    public static SeriesExtender Extender(SeriesHarness h, ListLogger<SeriesExtender>? log = null)
    {
        var service = new Mock<TableServiceClient>();
        service.Setup(s => s.GetTableClient(TableNames.Series)).Returns(h.SeriesTable.Object);
        return new SeriesExtender(service.Object, h.SeriesStore, h.Occurrences, log ?? new ListLogger<SeriesExtender>());
    }

    /// <summary>A stored row as text: every property, sorted by name, and its eTag. Two equal texts are the same row.</summary>
    public static string Snapshot(TableEntity row) =>
        row.RowKey + " etag=" + row.ETag + " " + string.Join("; ", row
            .Where(p => p.Key != "odata.etag")
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key + "=" + Convert.ToString(p.Value, CultureInfo.InvariantCulture)));

    /// <summary>Every row of a table as text, by row key.</summary>
    public static Dictionary<string, string> Snapshot(IEnumerable<KeyValuePair<string, TableEntity>> rows) =>
        rows.ToDictionary(r => r.Key, r => Snapshot(r.Value));

    /// <summary>Insert, upsert, update and delete calls that reached a table.</summary>
    public static int Writes(Mock<TableClient> table) =>
        table.Invocations.Count(i => i.Method.Name is nameof(TableClient.AddEntityAsync) or nameof(TableClient.UpsertEntityAsync)
            or nameof(TableClient.UpdateEntityAsync) or nameof(TableClient.DeleteEntityAsync));

    /// <summary>Makes a read of the whole Series table answer with these rows, in this order.</summary>
    public static void ListSeriesInOrder(SeriesHarness h, Func<IEnumerable<TableEntity>> rows) =>
        h.SeriesTable.Setup(t => t.QueryAsync(It.IsAny<Expression<Func<TableEntity, bool>>>(), It.IsAny<int?>(),
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(() => AsyncPageable<TableEntity>.FromPages([Page<TableEntity>.FromValues(rows().ToList(), null, Mock.Of<Response>())]));

    /// <summary>The storage error a busy table answers with.</summary>
    public static RequestFailedException Busy() => new(503, "The server is busy.", "ServerBusy", null);
}
