using System.Globalization;
using System.Text;
using Azure.Monitor.Query.Logs.Models;

namespace MonitoringApp;

internal static class KqlResultNormalizer
{
    internal static FreeKqlResult NormalizeRawTables(LogsQueryResult result, string workspace, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Error is not null)
            throw new InvalidOperationException($"Log Analytics query failed: {result.Error.Code}: {result.Error.Message}");
        var tables = new List<AnalysisQueryTable>();
        var rowsLeft = KqlQueryLimits.MaxTableRows;
        var bytesLeft = KqlQueryLimits.MaxTableResponseBytes;
        var limited = false;
        foreach (var table in result.AllTables)
        {
            var columns = table.Columns.Select(column => new AnalysisQueryColumn(column.Name, column.Type.ToString())).ToArray();
            var columnBytes = columns.Sum(column => (long)Encoding.UTF8.GetByteCount(column.Name) + Encoding.UTF8.GetByteCount(column.Type));
            if (columnBytes > bytesLeft) { limited = true; break; }
            bytesLeft -= (int)columnBytes;
            var rows = new List<IReadOnlyList<string?>>();
            foreach (var row in table.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rowsLeft == 0) { limited = true; break; }
                var cells = Enumerable.Range(0, row.Count).Select(index => FormatCell(row[index])).ToArray();
                var bytes = cells.Sum(cell => cell is null ? 0L : Encoding.UTF8.GetByteCount(cell));
                if (bytes > bytesLeft) { limited = true; break; }
                rows.Add(cells);
                rowsLeft--;
                bytesLeft -= (int)bytes;
            }
            tables.Add(new(workspace, columns, rows));
            if (limited) break;
        }
        return new(tables, limited);
    }

    internal static string? FormatCell(object? value) => value switch
    {
        null => null,
        DateTimeOffset timestamp => timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        DateTime timestamp => timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };

    internal static AnalysisQueryRunResult Normalize(LogsTable table, string workspace, string visualizationType)
    {
        var limit = visualizationType == AnalysisQueryVisualizationTypes.Table ? KqlQueryLimits.MaxTableRows : int.MaxValue;
        var normalized = Normalize(new AnalysisQueryTable(
            workspace,
            table.Columns.Select(column => new AnalysisQueryColumn(column.Name, column.Type.ToString())).ToArray(),
            table.Rows.Take(limit).Select(row => (IReadOnlyList<string?>)Enumerable.Range(0, row.Count)
                .Select(index => FormatCell(row[index])).ToArray()).ToArray()), visualizationType);
        return visualizationType == AnalysisQueryVisualizationTypes.Table && table.Rows.Count > KqlQueryLimits.MaxTableRows
            ? normalized with
            {
                Warning = $"Table result limited to {KqlQueryLimits.MaxTableRows} rows for this workspace. More rows exist; narrow the filters or time range to inspect them.",
                WarningNotice = new(AnalysisQueryNoticeCode.TableRowLimit)
            }
            : normalized;
    }

    internal static AnalysisQueryRunResult Normalize(AnalysisQueryTable table, string visualizationType)
    {
        if (!AnalysisQueryVisualizationTypes.All.Contains(visualizationType))
            return new([], "Unsupported visualization type.") { ErrorNotice = new(AnalysisQueryNoticeCode.UnsupportedVisualization) };
        if (table.Rows.Any(row => row.Count != table.Columns.Count))
            return new([], "The query returned inconsistent table rows.") { ErrorNotice = new(AnalysisQueryNoticeCode.InconsistentRows) };
        if (visualizationType == AnalysisQueryVisualizationTypes.Table)
            return new([], null, [table]);
        var names = table.Columns.Select(column => column.Name).ToList();
        var timestampIndex = names.IndexOf("Timestamp");
        var seriesIndex = names.IndexOf("Series");
        var valueIndex = names.IndexOf("Value");
        if (visualizationType == AnalysisQueryVisualizationTypes.Stat)
        {
            if (seriesIndex < 0 || valueIndex < 0 ||
                table.Rows.Any(row => string.IsNullOrWhiteSpace(row[seriesIndex]) ||
                    row[valueIndex] is not null && (!double.TryParse(row[valueIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var statValue) || !double.IsFinite(statValue))))
                return new([], "Stat queries must return Series and numeric or null Value columns.") { ErrorNotice = new(AnalysisQueryNoticeCode.InvalidStatColumns) };
            return new([], null, [table]);
        }
        if (seriesIndex < 0 || valueIndex < 0 ||
            (visualizationType is AnalysisQueryVisualizationTypes.LineChart or AnalysisQueryVisualizationTypes.StatusHistory && timestampIndex < 0))
            return new([], "The query must return Timestamp, Series and Value for lines/status history, or Series and Value for honeycombs/bars/gauges.")
            { ErrorNotice = new(AnalysisQueryNoticeCode.MissingColumns) };
        var rows = new List<AnalysisQueryNormalizedRow>();
        foreach (var row in table.Rows)
        {
            if (string.IsNullOrWhiteSpace(row[seriesIndex]) ||
                !double.TryParse(row[valueIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                !double.IsFinite(value))
                return new([], "The query returned invalid numeric values or series names.") { ErrorNotice = new(AnalysisQueryNoticeCode.InvalidSeriesValues) };
            var timestamp = DateTimeOffset.UnixEpoch;
            if (timestampIndex >= 0 &&
                !DateTimeOffset.TryParse(row[timestampIndex], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out timestamp))
                return new([], "The query returned an invalid Timestamp.") { ErrorNotice = new(AnalysisQueryNoticeCode.InvalidTimestamp) };
            rows.Add(new(timestamp.ToUniversalTime(), row[seriesIndex]!, value,
                Cell("Label"), Cell("Dimension")));
            string? Cell(string name) => names.IndexOf(name) is var index && index >= 0 ? row[index] : null;
        }
        return new(rows, null, [table]);
    }
}

public sealed record AnalysisQueryNormalizedRow(
    DateTimeOffset Timestamp,
    string Series,
    double Value,
    string? Label,
    string? Dimension);

public sealed record AnalysisQueryRunResult(
    IReadOnlyList<AnalysisQueryNormalizedRow> Rows,
    string? Error,
    IReadOnlyList<AnalysisQueryTable>? Tables = null,
    string? Warning = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public AnalysisQueryNotice? ErrorNotice { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public AnalysisQueryNotice? WarningNotice { get; init; }
}
