using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitoringApp;

public sealed record UiLanguageDefinition
{
    public required string Culture { get; init; }
    public required string DisplayName { get; init; }
    public required string File { get; init; }
    public required bool Enabled { get; init; }
}

internal sealed record UiLanguageConfiguration
{
    internal const string FileName = "languages.json";
    internal const int MaximumFileBytes = 64 * 1024;
    internal const int MaximumLanguages = 32;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };
    private static readonly FrozenDictionary<string, CultureInfo> KnownCultures =
        CultureInfo.GetCultures(CultureTypes.AllCultures).Where(culture => culture.Name.Length > 0)
            .ToFrozenDictionary(culture => culture.Name, StringComparer.OrdinalIgnoreCase);

    public required int Version { get; init; }
    public required string DefaultCulture { get; init; }
    public required IReadOnlyList<UiLanguageDefinition> Languages { get; init; }

    internal static IList<CultureInfo> AvailableCultures() => KnownCultures.Values.ToArray();

    internal static UiLanguageConfiguration Parse(string json)
    {
        var configuration = JsonSerializer.Deserialize<UiLanguageConfiguration>(json, SerializerOptions)
            ?? throw new InvalidDataException("The UI language registry must be a JSON object.");
        if (configuration.Version != 1)
            throw new InvalidDataException("Unsupported UI language registry version.");
        if (configuration.Languages is null || configuration.Languages.Count is < 1 or > MaximumLanguages)
            throw new InvalidDataException($"The UI language registry must contain 1 to {MaximumLanguages} languages.");

        var languages = new List<UiLanguageDefinition>();
        var cultures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in configuration.Languages)
        {
            if (language is null || string.IsNullOrEmpty(language.Culture) ||
                !KnownCultures.TryGetValue(language.Culture, out var culture))
                throw new InvalidDataException("The UI language registry contains an unsupported culture.");
            if (!cultures.Add(culture.Name))
                throw new InvalidDataException($"Duplicate UI culture '{culture.Name}'.");
            if (string.IsNullOrWhiteSpace(language.DisplayName) || language.DisplayName.Length > 128 ||
                language.DisplayName.Any(char.IsControl))
                throw new InvalidDataException($"UI culture '{culture.Name}' has an invalid display name.");
            if (!IsPackFileName(language.File) || !files.Add(language.File))
                throw new InvalidDataException($"UI culture '{culture.Name}' has an invalid or duplicate language filename.");
            languages.Add(language with { Culture = culture.Name });
        }

        if (!cultures.Contains(UiLocalization.English))
            throw new InvalidDataException("The UI language registry must include the en-US fallback pack, even if its selection is disabled.");
        var selectedDefault = languages.FirstOrDefault(language => language.Enabled &&
            string.Equals(language.Culture, configuration.DefaultCulture, StringComparison.OrdinalIgnoreCase));
        if (selectedDefault is null)
            throw new InvalidDataException("The default UI culture must reference an enabled language.");
        return configuration with
        {
            DefaultCulture = selectedDefault.Culture,
            Languages = languages.AsReadOnly()
        };
    }

    private static bool IsPackFileName(string? file)
    {
        if (string.IsNullOrEmpty(file) || file.Length > 128 ||
            !char.IsAsciiLetterOrDigit(file[0]) || !file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            file.Equals(FileName, StringComparison.OrdinalIgnoreCase) ||
            file.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_')))
            return false;
        var stem = file.Split('.')[0];
        return !new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }
            .Contains(stem, StringComparer.OrdinalIgnoreCase);
    }
}
