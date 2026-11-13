using System.Collections.Frozen;
using System.Text;
using System.Text.Json;

namespace MonitoringApp;

public sealed partial class JsonUiLanguageStore
{
    private static readonly JsonSerializerOptions AdminJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    internal UiLanguageAdminState AdminLoad()
    {
        lock (gate) return State(Healthy());
    }

    private JsonUiLanguageSnapshot Healthy(string? expected = null)
    {
        JsonUiLanguageSnapshot snapshot;
        try { snapshot = Read(); }
        catch (InvalidOperationException exception) when (exception.InnerException is IOException or UnauthorizedAccessException
            or InvalidDataException or JsonException or DecoderFallbackException or FormatException)
        { throw new UiLanguageAdminException(UiLanguageAdminError.Unhealthy, exception); }
        if (snapshot.HasReloadFailure)
            throw new UiLanguageAdminException(UiLanguageAdminError.Unhealthy);
        if (expected is not null && snapshot.Revision != expected)
            throw new UiLanguageAdminException(UiLanguageAdminError.Conflict);
        return snapshot;
    }

    private UiLanguageAdminState State(JsonUiLanguageSnapshot snapshot) => new(snapshot.Revision,
        snapshot.Configuration.DefaultCulture, snapshot.Configuration.Languages,
        JsonSerializer.Serialize(snapshot.Configuration, AdminJson),
        File.Exists(Path.Combine(directory, HistoryName(snapshot.Revision))));

    internal UiLanguageDownload AdminDownload(string expected, string culture)
    {
        lock (gate)
        {
            var snapshot = Healthy(expected);
            var language = snapshot.Configuration.Languages.FirstOrDefault(entry =>
                string.Equals(entry.Culture, culture, StringComparison.OrdinalIgnoreCase))
                ?? throw new UiLanguageAdminException(UiLanguageAdminError.Configuration);
            var bytes = ReadFile(language.File, MaximumFileBytes);
            _ = Healthy(expected);
            return new(language.File, bytes);
        }
    }

    internal UiLanguagePreview AdminPreview(string expected, string selectedDefault, IReadOnlyList<UiLanguageDefinition> languages,
        IReadOnlyList<UiLanguageUpload> uploads)
    {
        lock (gate)
        {
            _ = Healthy(expected);
            return Prepare(expected, new UiLanguageConfiguration { Version = 1, DefaultCulture = selectedDefault, Languages = languages }, uploads, false);
        }
    }

    internal UiLanguagePreview AdminRollback(string expected)
    {
        lock (gate)
        {
            _ = Healthy(expected);
            var file = HistoryName(expected);
            if (!File.Exists(Path.Combine(directory, file)))
                throw new UiLanguageAdminException(UiLanguageAdminError.NoPrevious);
            return Prepare(expected, Configuration(ReadFile(file, UiLanguageConfiguration.MaximumFileBytes)), [], true);
        }
    }

    private static UiLanguageConfiguration Configuration(byte[] bytes)
    {
        try { return UiLanguageConfiguration.Parse(Decode(bytes)); }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or DecoderFallbackException)
        { throw new UiLanguageAdminException(UiLanguageAdminError.Configuration, exception); }
    }

    private UiLanguagePreview Prepare(string expected, UiLanguageConfiguration proposed,
        IReadOnlyList<UiLanguageUpload> uploads, bool rollback)
    {
        if (uploads.Count > UiLanguageConfiguration.MaximumLanguages ||
            uploads.Select(upload => upload.File).Distinct(StringComparer.OrdinalIgnoreCase).Count() != uploads.Count)
            throw new UiLanguageAdminException(UiLanguageAdminError.Configuration);
        var configuration = Configuration(JsonSerializer.SerializeToUtf8Bytes(proposed, AdminJson));
        var registry = JsonSerializer.SerializeToUtf8Bytes(configuration, AdminJson);
        if (registry.Length > UiLanguageConfiguration.MaximumFileBytes)
            throw new UiLanguageAdminException(UiLanguageAdminError.TooLarge);
        var staged = uploads.ToDictionary(upload => upload.File, StringComparer.OrdinalIgnoreCase);
        if (staged.Keys.Any(file => !configuration.Languages.Any(language => language.File.Equals(file, StringComparison.OrdinalIgnoreCase))))
            throw new UiLanguageAdminException(UiLanguageAdminError.Configuration);
        var bytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var reports = new List<UiLanguagePackReport>();
        foreach (var language in configuration.Languages)
        {
            var content = staged.TryGetValue(language.File, out var upload) ? upload.Bytes : ReadFile(language.File, MaximumFileBytes);
            if (content.Length > MaximumFileBytes)
                throw new UiLanguageAdminException(UiLanguageAdminError.TooLarge);
            bytes.Add(language.Culture, content);
            reports.Add(Inspect(content, language.Culture, language.File).Report);
        }
        var revision = Fingerprint(registry, configuration, bytes);
        if (uploads.Count == 0 && current is not null && reports.All(report => report.IsValid) &&
            JsonSerializer.SerializeToUtf8Bytes(current.Configuration, AdminJson).AsSpan().SequenceEqual(registry))
            revision = expected;
        return new(configuration, Array.AsReadOnly(uploads.ToArray()), registry, expected,
            revision, reports.AsReadOnly(), rollback);
    }

    internal UiLanguageAdminState AdminActivate(UiLanguagePreview preview)
    {
        lock (gate)
        {
            if (!preview.CanActivate) throw new UiLanguageAdminException(UiLanguageAdminError.ReviewRequired);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new UiLanguageAdminException(UiLanguageAdminError.Storage);
            var lockPath = Path.Combine(directory, ".languages.lock");
            RejectLink(lockPath);
            using var writeLock = AcquireLock(lockPath);
            var before = Healthy(preview.ExpectedRevision);
            var refreshed = Prepare(preview.ExpectedRevision, preview.Configuration, preview.Uploads, preview.IsRollback);
            if (!refreshed.CanActivate || refreshed.Revision != preview.Revision)
                throw new UiLanguageAdminException(UiLanguageAdminError.Conflict);

            var oldBytes = before.Configuration.Languages.ToDictionary(language => language.Culture,
                language => ReadFile(language.File, MaximumFileBytes), StringComparer.OrdinalIgnoreCase);
            if (Fingerprint(ReadFile(UiLanguageConfiguration.FileName, UiLanguageConfiguration.MaximumFileBytes),
                before.Configuration, oldBytes) != before.Revision)
                throw new UiLanguageAdminException(UiLanguageAdminError.Conflict);
            foreach (var upload in preview.Uploads)
                WriteNew(upload.File, upload.Bytes);
            var savedLanguages = new List<UiLanguageDefinition>();
            foreach (var language in before.Configuration.Languages)
            {
                var name = $"ui-saved-{Guid.NewGuid():N}.json";
                WriteNew(name, oldBytes[language.Culture]);
                savedLanguages.Add(language with { File = name });
            }
            var previous = before.Configuration with { Languages = savedLanguages.AsReadOnly() };
            // History is addressed by the candidate revision; replacing the live manifest is the only activation point.
            WriteAtomic(HistoryName(preview.Revision), JsonSerializer.SerializeToUtf8Bytes(previous, AdminJson));
            _ = Healthy(preview.ExpectedRevision);
            WriteAtomic(UiLanguageConfiguration.FileName, preview.RegistryBytes);
            attemptedFingerprint = null;
            var active = Read();
            if (active.HasReloadFailure || active.Revision != preview.Revision)
                throw new UiLanguageAdminException(UiLanguageAdminError.Unconfirmed);
            return State(active);
        }
    }

    private static string HistoryName(string revision) => $"ui-history-{revision}.json";

    private static FileStream AcquireLock(string path)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33 or 11)
        { throw new UiLanguageAdminException(UiLanguageAdminError.Conflict, exception); }
    }

    private static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null ||
            File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UiLanguageAdminException(UiLanguageAdminError.Storage);
    }

    private void WriteNew(string name, byte[] bytes)
    {
        var path = Path.Combine(directory, name);
        RejectLink(path);
        if (File.Exists(path))
        {
            if (!ReadFile(name, MaximumFileBytes).AsSpan().SequenceEqual(bytes))
                throw new UiLanguageAdminException(UiLanguageAdminError.Conflict);
            return;
        }
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private void WriteAtomic(string name, byte[] bytes)
    {
        var path = Path.Combine(directory, name);
        RejectLink(path);
        var temporary = Path.Combine(directory, $".language-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private (FrozenDictionary<string, string> Values, UiLanguagePackReport Report) Inspect(byte[] bytes, string culture, string file)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var issues = new List<UiLanguagePackIssue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(Decode(bytes), new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                issues.Add(new(UiLanguagePackIssueCode.Json, ""));
            else foreach (var property in document.RootElement.EnumerateObject())
            {
                var key = property.Name;
                if (!seen.Add(key)) { issues.Add(new(UiLanguagePackIssueCode.DuplicateKey, key)); continue; }
                if (!canonicalEnglish.TryGetValue(key, out var baseline)) { issues.Add(new(UiLanguagePackIssueCode.UnknownKey, key)); continue; }
                if (property.Value.ValueKind != JsonValueKind.String) { issues.Add(new(UiLanguagePackIssueCode.ValueType, key)); continue; }
                var value = property.Value.GetString()!;
                if (value.Length > MaximumTextLength) { issues.Add(new(UiLanguagePackIssueCode.TextLength, key)); continue; }
                if (string.IsNullOrWhiteSpace(value))
                {
                    if (culture == UiLocalization.English) issues.Add(new(UiLanguagePackIssueCode.EmptyEnglish, key));
                    continue;
                }
                try
                {
                    if (!ParameterIndexes(baseline).SetEquals(ParameterIndexes(value)))
                    { issues.Add(new(UiLanguagePackIssueCode.Parameters, key)); continue; }
                }
                catch (FormatException) { issues.Add(new(UiLanguagePackIssueCode.Parameters, key)); continue; }
                values.Add(key, value);
            }
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        { issues.Add(new(UiLanguagePackIssueCode.Json, "")); }
        var missing = canonicalEnglish.Keys.Except(values.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (culture == UiLocalization.English)
            issues.AddRange(missing.Select(key => new UiLanguagePackIssue(UiLanguagePackIssueCode.MissingEnglish, key)));
        return (values.ToFrozenDictionary(StringComparer.Ordinal),
            new(culture, file, Array.AsReadOnly(missing), issues.AsReadOnly()));
    }
}
