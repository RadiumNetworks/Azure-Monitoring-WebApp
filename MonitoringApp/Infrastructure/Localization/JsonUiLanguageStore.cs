using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MonitoringApp;

public sealed partial class JsonUiLanguageStore(string directory, ILogger<JsonUiLanguageStore> logger)
{
    internal const int MaximumFileBytes = 2 * 1024 * 1024;
    internal const int MaximumTextLength = 16 * 1024;
    private readonly object gate = new();
    private JsonUiLanguageSnapshot? current;
    private string? attemptedFingerprint;
    private string? reportedFailure;
    private readonly IReadOnlyDictionary<string, string> canonicalEnglish = UiResources.GetEnglishEntries();

    public void Validate() => _ = Read();

    internal JsonUiLanguageSnapshot Read()
    {
        lock (gate)
        {
            string? fingerprint = null;
            try
            {
                var registryBytes = ReadFile(UiLanguageConfiguration.FileName, UiLanguageConfiguration.MaximumFileBytes);
                var configuration = UiLanguageConfiguration.Parse(Decode(registryBytes));
                var packBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var language in configuration.Languages)
                {
                    var bytes = ReadFile(language.File, MaximumFileBytes);
                    packBytes.Add(language.Culture, bytes);
                }
                fingerprint = Fingerprint(registryBytes, configuration, packBytes);
                if (current is not null && fingerprint == attemptedFingerprint)
                    return current;

                attemptedFingerprint = fingerprint;
                var packs = configuration.Languages.ToFrozenDictionary(language => language.Culture,
                    language => (IReadOnlyDictionary<string, string>)Parse(packBytes[language.Culture], language.Culture),
                    StringComparer.OrdinalIgnoreCase);
                current = new JsonUiLanguageSnapshot(configuration, packs,
                    configuration.Languages.Where(language => language.Enabled)
                        .ToFrozenDictionary(language => language.Culture, StringComparer.OrdinalIgnoreCase),
                    fingerprint, false);
                reportedFailure = null;
                foreach (var language in configuration.Languages)
                {
                    var missing = canonicalEnglish.Count - packs[language.Culture].Count;
                    if (missing > 0)
                        logger.LogWarning("UI language {Culture} is missing {Count} translations; using the current English JSON texts for those keys.",
                            language.Culture, missing);
                }
                return current;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or JsonException or DecoderFallbackException or FormatException or InvalidDataException)
            {
                attemptedFingerprint = fingerprint;
                if (current is null)
                {
                    logger.LogCritical(exception, "Initial UI language JSON validation failed.");
                    throw new InvalidOperationException("The initial UI language JSON files are invalid or unavailable.", exception);
                }

                var failure = fingerprint ?? $"{exception.GetType().Name}:{exception.Message}";
                if (reportedFailure != failure)
                    logger.LogError(exception, "UI language JSON reload rejected; retaining the last validated language snapshot.");
                reportedFailure = failure;
                current = current with { HasReloadFailure = true };
                return current;
            }
        }
    }

    private byte[] ReadFile(string name, int maximumBytes)
    {
        var path = Path.Combine(directory, name);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("UI language files must not be symbolic links or reparse points.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = file.Length;
        if (length > maximumBytes)
            throw new InvalidDataException($"The UI language file '{name}' exceeds {maximumBytes} bytes.");
        var bytes = new byte[checked((int)length)];
        file.ReadExactly(bytes);
        if (file.ReadByte() != -1)
            throw new IOException($"The UI language file '{name}' changed while being read.");
        return bytes;
    }

    private FrozenDictionary<string, string> Parse(byte[] bytes, string culture)
    {
        var inspection = Inspect(bytes, culture, "");
        if (!inspection.Report.IsValid)
        {
            var issue = inspection.Report.Issues[0];
            throw new InvalidDataException($"UI resource validation failed for '{culture}': {issue.Code}, '{issue.Key}'.");
        }
        return inspection.Values;
    }

    private static string Fingerprint(byte[] registry, UiLanguageConfiguration configuration, IReadOnlyDictionary<string, byte[]> packs)
    {
        var hashes = new StringBuilder(Convert.ToHexString(SHA256.HashData(registry)));
        foreach (var language in configuration.Languages)
            hashes.Append(Convert.ToHexString(SHA256.HashData(packs[language.Culture])));
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(hashes.ToString())));
    }

    private static string Decode(byte[] bytes)
    {
        var offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        return new UTF8Encoding(false, true).GetString(bytes.AsSpan(offset));
    }

    internal static HashSet<int> ParameterIndexes(string value)
    {
        var format = CompositeFormat.Parse(value);
        if (format.MinimumArgumentCount > 32)
            throw new FormatException("UI format argument indexes must be less than 32.");

        var indexes = new HashSet<int>();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '{') continue;
            if (value[i + 1] == '{')
            {
                i++;
                continue;
            }
            var start = ++i;
            while (char.IsAsciiDigit(value[i])) i++;
            indexes.Add(int.Parse(value.AsSpan(start, i - start), CultureInfo.InvariantCulture));
            while (value[i] == ' ') i++;
            if (value[i] == ',')
            {
                i++;
                while (value[i] == ' ') i++;
                if (value[i] == '-') i++;
                start = i;
                while (char.IsAsciiDigit(value[i])) i++;
                if (!int.TryParse(value.AsSpan(start, i - start), out var alignment) || alignment > 4096)
                    throw new FormatException("UI format alignment must not exceed 4096.");
            }
            while (value[i] != ':' && value[i] != '}') i++;
            if (value[i] == ':')
            {
                start = ++i;
                while (value[i] != '}') i++;
                var specification = value.AsSpan(start, i - start);
                if (specification.Length > 128 ||
                    (specification.Length > 1 && char.IsAsciiLetter(specification[0]) &&
                     specification[1..].IndexOfAnyExceptInRange('0', '9') < 0 &&
                     (!int.TryParse(specification[1..], out var precision) || precision > 100)))
                    throw new FormatException("UI format specification exceeds the supported limits.");
            }
        }
        return indexes;
    }
}

internal sealed record JsonUiLanguageSnapshot(
    UiLanguageConfiguration Configuration,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Packs,
    IReadOnlyDictionary<string, UiLanguageDefinition> ActiveLanguages,
    string Revision,
    bool HasReloadFailure)
{
    internal IReadOnlyDictionary<string, string> English => Packs[UiLocalization.English];
}
