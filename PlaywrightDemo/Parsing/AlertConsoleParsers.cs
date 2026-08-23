using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PlaywrightDemo.Models;

namespace PlaywrightDemo.Parsing;

public static partial class AlertConsoleParsers
{
    public static ResultSummary ParseResultSummary(string text)
    {
        var match = ResultSummaryPattern().Match(text);
        if (!match.Success)
        {
            throw new FormatException($"Unexpected result summary: {text}");
        }

        return new ResultSummary(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
    }

    public static NavigationNode ParseNavigationNode(string text)
    {
        var match = NavigationNodePattern().Match(text.Trim());
        if (!match.Success)
        {
            throw new FormatException($"Unexpected navigation node: {text}");
        }

        return new NavigationNode(match.Groups[1].Value.Trim(), int.Parse(match.Groups[2].Value));
    }

    public static ChartSummary ParseChartDescription(string text)
    {
        var match = ChartDescriptionPattern().Match(text);
        if (!match.Success)
        {
            throw new FormatException($"Unexpected chart description: {text}");
        }

        return new ChartSummary(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
    }

    public static DateTimeOffset ParseUtcTooltip(string text)
    {
        var normalized = UtcSuffixPattern().Replace(text, string.Empty);
        return DateTimeOffset.Parse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    }

    public static JsonObject ParseCommonAlertPayload(string text)
    {
        return JsonNode.Parse(text) as JsonObject
            ?? throw new FormatException("The raw payload must be a JSON object.");
    }

    public static string? TargetNameFromPayload(JsonObject payload)
    {
        var criteria = payload["data"]?["alertContext"]?["condition"]?["allOf"]?.AsArray();
        var dimensionValue = criteria?
            .SelectMany(criterion => criterion?["dimensions"]?.AsArray() ?? new JsonArray())
            .Select(dimension => dimension?["value"]?.GetValue<string>())
            .FirstOrDefault(IsMeaningfulTarget);

        var items = payload["data"]?["essentials"]?["configurationItems"]?.AsArray();
        var configurationItem = items?
            .Select(node => node?.GetValue<string>())
            .FirstOrDefault(IsMeaningfulTarget);

        var targets = payload["data"]?["essentials"]?["alertTargetIDs"]?.AsArray();
        var alertTarget = targets?
            .Select(node => node?.GetValue<string>())
            .FirstOrDefault(IsMeaningfulTarget);

        return (dimensionValue ?? configurationItem ?? alertTarget)?
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();
    }

    private static bool IsMeaningfulTarget(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Equals("<EMPTY_VALUE>", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"Showing\s+(\d+)\s+of\s+(\d+)\s+alerts?", RegexOptions.IgnoreCase)]
    private static partial Regex ResultSummaryPattern();

    [GeneratedRegex(@"^(.*?)\s+(\d+)$", RegexOptions.Singleline)]
    private static partial Regex NavigationNodePattern();

    [GeneratedRegex(@"(\d+)\s+total.*maximum of\s+(\d+)\s+in one hour", RegexOptions.IgnoreCase)]
    private static partial Regex ChartDescriptionPattern();

    [GeneratedRegex(@"\s+UTC$", RegexOptions.IgnoreCase)]
    private static partial Regex UtcSuffixPattern();
}