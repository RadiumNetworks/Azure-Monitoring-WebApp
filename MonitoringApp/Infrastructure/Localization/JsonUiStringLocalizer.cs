using System.Globalization;
using Microsoft.Extensions.Localization;

namespace MonitoringApp;

internal sealed class JsonUiStringLocalizer(UiLanguageContext languages) : IStringLocalizer<UiResources>
{
    internal bool HasReloadFailure => languages.Snapshot.HasReloadFailure;

    public LocalizedString this[string name]
    {
        get
        {
            if (languages.Translations(CultureInfo.CurrentUICulture.Name).TryGetValue(name, out var translated))
                return new LocalizedString(name, translated);
            return languages.Snapshot.English.TryGetValue(name, out var english)
                ? new LocalizedString(name, english)
                : new LocalizedString(name, name, resourceNotFound: true);
        }
    }

    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            var text = this[name];
            return new LocalizedString(name,
                string.Format(CultureInfo.CurrentUICulture, text.Value, arguments), text.ResourceNotFound);
        }
    }

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
    {
        var translations = languages.Translations(CultureInfo.CurrentUICulture.Name);
        return !includeParentCultures
            ? translations.Select(entry => new LocalizedString(entry.Key, entry.Value))
            : languages.Snapshot.English.Select(entry => new LocalizedString(entry.Key,
                translations.TryGetValue(entry.Key, out var translated) ? translated : entry.Value));
    }
}
