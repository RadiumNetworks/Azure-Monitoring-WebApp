using System.Text.Json;

namespace AlertConsole.Configuration;

public static class AlertConsoleUrlResolver
{
    public const string EnvironmentVariableName = "ALERT_CONSOLE_URL";
    public const string LocalFileName = "alertconsole.local.json";

    public static string Resolve(string? explicitUrl = null)
    {
        var configuredUrl = FirstNonEmpty(
            explicitUrl,
            Environment.GetEnvironmentVariable(EnvironmentVariableName),
            ReadLocalUrl());

        if (configuredUrl is null)
        {
            throw new InvalidOperationException(
                $"Alert Console URL is required. Set {EnvironmentVariableName} or create {LocalFileName}.");
        }

        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Alert Console URL must be an absolute HTTP or HTTPS URL.");
        }

        return configuredUrl;
    }

    private static string? ReadLocalUrl()
    {
        var path = Path.Combine(AppContext.BaseDirectory, LocalFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("alertConsoleUrl", out var value)
                ? value.GetString()
                : null;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"{LocalFileName} contains invalid JSON.", exception);
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}