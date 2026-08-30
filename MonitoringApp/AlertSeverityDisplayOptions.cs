namespace MonitoringApp;

/// <summary>
/// Configures how severity labels are rendered beside alert names in the Inbox.
/// </summary>
public sealed class AlertSeverityDisplayOptions
{
    public const string SectionName = "AlertSeverityDisplay";

    private static readonly HashSet<string> SupportedColors =
        new(["green", "yellow", "red", "gray", "black"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SupportedFontStyles =
        new(["bold", "normal"], StringComparer.OrdinalIgnoreCase);

    public AlertSeverityStyle[] Severities { get; init; } = [];
    public AlertSeverityStyle Default { get; init; } = new()
    {
        Color = "black",
        FontStyle = "normal"
    };

    /// <summary>
    /// Returns safe, validated CSS classes for a severity. Unknown values use the configured default.
    /// </summary>
    public string CssClass(string severity)
    {
        var style = Severities.FirstOrDefault(candidate =>
            candidate.Severity.Equals(severity, StringComparison.OrdinalIgnoreCase)) ?? Default;
        return $"severity-color-{style.Color.ToLowerInvariant()} severity-style-{style.FontStyle.ToLowerInvariant()}";
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Severities.Length == 0)
        {
            errors.Add($"{SectionName}:Severities must contain at least one severity style.");
        }

        if (Severities.Any(style => string.IsNullOrWhiteSpace(style.Severity)))
        {
            errors.Add($"{SectionName}:Severities must not contain an empty Severity value.");
        }

        if (Severities
            .Where(style => !string.IsNullOrWhiteSpace(style.Severity))
            .Select(style => style.Severity)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != Severities.Count(style => !string.IsNullOrWhiteSpace(style.Severity)))
        {
            errors.Add($"{SectionName}:Severities must not contain duplicate Severity values.");
        }

        foreach (var style in Severities.Append(Default))
        {
            if (!SupportedColors.Contains(style.Color))
            {
                errors.Add(
                    $"{SectionName}: Color '{style.Color}' is invalid. Use green, yellow, red, gray, or black.");
            }

            if (!SupportedFontStyles.Contains(style.FontStyle))
            {
                errors.Add(
                    $"{SectionName}: FontStyle '{style.FontStyle}' is invalid. Use bold or normal.");
            }
        }

        return errors;
    }
}

public sealed class AlertSeverityStyle
{
    public string Severity { get; init; } = string.Empty;
    public string Color { get; init; } = string.Empty;
    public string FontStyle { get; init; } = string.Empty;
}