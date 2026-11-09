using System.Globalization;
using Microsoft.Extensions.Localization;

namespace MonitoringApp;

public sealed class UiText(IStringLocalizer<UiResources> localizer, ILogger<UiText> logger)
{
    public string Format(string key, params object[] arguments) =>
        string.Format(CultureInfo.CurrentUICulture, this[key], arguments);

    public string Plural(string key, int count) =>
        Format(key + (count == 1 ? ".One" : ".Other"), count);

    public string this[string key]
    {
        get
        {
            string english;
            try
            {
                english = UiResources.GetEnglish(key);
            }
            catch (InvalidOperationException exception)
            {
                logger.LogCritical(exception, "An English UI resource is missing or empty.");
                throw;
            }

            var localized = localizer[key];
            return localized.ResourceNotFound || string.IsNullOrWhiteSpace(localized.Value)
                ? english
                : localized.Value;
        }
    }
}
