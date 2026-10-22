using System.IO.Compression;
using System.Text.Json;

namespace MonitoringApp;

public interface IAnalysisQueryExportService
{
    Task<byte[]> ExportAsync(CancellationToken cancellationToken = default);
}

public sealed class AnalysisQueryExportService(AnalysisQueryRegistry registry) : IAnalysisQueryExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly HashSet<string> ReservedFileNames = new(
        ["con", "prn", "aux", "nul", .. Enumerable.Range(1, 9).Select(index => $"com{index}"),
            .. Enumerable.Range(1, 9).Select(index => $"lpt{index}")], StringComparer.OrdinalIgnoreCase);

    public async Task<byte[]> ExportAsync(CancellationToken cancellationToken = default)
    {
        var queries = await registry.ListAsync(cancellationToken);
        if (queries.Count == 0)
            throw new AnalysisQueryExportException("No saved queries are available to export.");

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var query in queries.OrderBy(query => query.Key, StringComparer.OrdinalIgnoreCase).ThenBy(query => query.Id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AnalysisQuerySnapshot snapshot;
                try
                {
                    snapshot = AnalysisQueryRegistry.CreateSnapshot(query);
                }
                catch (InvalidOperationException exception)
                {
                    throw new AnalysisQueryExportException(
                        $"Saved query '{query.Key}' has invalid visualization options. Correct them before exporting.", exception);
                }
                var entry = archive.CreateEntry(CreateFileName(query.Key, fileNames), CompressionLevel.Optimal);
                await using var stream = entry.Open();
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken);
            }
        }
        return output.ToArray();
    }

    private static string CreateFileName(string key, HashSet<string> used)
    {
        var name = new string(key.Trim().ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_').ToArray());
        if (name.Length == 0 || ReservedFileNames.Contains(name))
            name = $"query-{name}";
        var fileName = $"{name}.json";
        var suffix = 2;
        while (!used.Add(fileName))
            fileName = $"{name}-{suffix++}.json";
        return fileName;
    }
}

public sealed class AnalysisQueryExportException(string message, Exception? innerException = null)
    : Exception(message, innerException);
