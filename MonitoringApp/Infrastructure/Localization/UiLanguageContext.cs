namespace MonitoringApp;

public sealed class UiLanguageContext
{
    public UiLanguageContext(JsonUiLanguageStore store)
    {
        Snapshot = store.Read();
        EnabledLanguages = Array.AsReadOnly(Snapshot.Configuration.Languages.Where(language => language.Enabled).ToArray());
    }

    internal JsonUiLanguageSnapshot Snapshot { get; }
    public IReadOnlyList<UiLanguageDefinition> EnabledLanguages { get; }
    public string DefaultCulture => Snapshot.Configuration.DefaultCulture;
    public string Revision => Snapshot.Revision;

    public string? CanonicalCulture(string? culture) =>
        culture is not null && Snapshot.ActiveLanguages.TryGetValue(culture, out var language)
            ? language.Culture : null;

    internal IReadOnlyDictionary<string, string> Translations(string culture) =>
        Snapshot.Packs[CanonicalCulture(culture) ?? DefaultCulture];
}
