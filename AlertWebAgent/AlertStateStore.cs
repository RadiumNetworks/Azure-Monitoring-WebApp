using System.Text.Json;

namespace AlertWebAgent;

public sealed class AlertStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<AlertState> LoadAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return new AlertState(false, new HashSet<string>(StringComparer.Ordinal));
        }

        await using var stream = File.OpenRead(fullPath);
        var ids = await JsonSerializer.DeserializeAsync<string[]>(stream, cancellationToken: cancellationToken) ?? [];
        return new AlertState(true, new HashSet<string>(ids, StringComparer.Ordinal));
    }

    public async Task SaveAsync(string path, HashSet<string> ids, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);

        var temporaryPath = fullPath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                ids.Order(StringComparer.Ordinal).ToArray(),
                JsonOptions,
                cancellationToken);
        }

        File.Move(temporaryPath, fullPath, true);
    }
}

public sealed class AlertState(bool existed, HashSet<string> seenIds)
{
    public bool Existed { get; set; } = existed;
    public HashSet<string> SeenIds { get; } = seenIds;
}