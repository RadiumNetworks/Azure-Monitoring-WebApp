using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MonitoringApp;

public sealed class QueryResultPresenter
{
    private readonly IReadOnlyDictionary<string, QueryResultDefinition> definitions;

    /// <summary>
    /// Loads all JSON alert definitions from the given directory. The loaded definitions are used to select presentation rules by query-result type.
    /// </summary>
    public QueryResultPresenter(string definitionsPath)
    {
        definitions = Directory.Exists(definitionsPath)
            ? Directory.EnumerateFiles(definitionsPath, "*.json")
                .Select(LoadDefinition)
                .Where(definition => !string.IsNullOrWhiteSpace(definition.Type))
                .ToDictionary(definition => definition.Type, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, QueryResultDefinition>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Converts an alert's raw JSON into the generic model used by the Search Result column. It returns null when the payload is invalid, incomplete, or has no matching definition.
    /// </summary>
    public QueryResultPresentation? Parse(string rawJson) => Parse(rawJson, null);

    /// <summary>
    /// Projects configured display labels for the UI only. Rules and prompt consumers must use Parse.
    /// </summary>
    public QueryResultPresentation? ParseForDisplay(string rawJson, UiText text) => Parse(rawJson, text);

    private QueryResultPresentation? Parse(string rawJson, UiText? text)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            if (!QueryResultPayload.TryGetQueryResult(document.RootElement, out var queryResult) ||
                !QueryResultPayload.TryGetString(queryResult, "type", out var type) ||
                !definitions.TryGetValue(type, out var definition) ||
                !QueryResultPayload.TryReadTable(queryResult, out var table))
            {
                return null;
            }

            var rows = ReadRows(table);

            return new QueryResultPresentation(
                DisplayLabel(definition.Label, definition.LabelKey, text),
                definition.CollapseRows,
                BuildSummary(definition.Summary, rows, text),
                rows.Select(row => BuildRow(definition.Row, row, text)).ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves the target and site for an alert using the identity columns and dimensions configured for its query-result type.
    /// </summary>
    public AlertDisplayIdentity ResolveIdentity(AlertRecord alert)
    {
        var fallbackTarget = alert.TargetName;
        if (string.IsNullOrWhiteSpace(alert.RawJson))
        {
            return new AlertDisplayIdentity(fallbackTarget, string.Empty);
        }

        try
        {
            using var document = JsonDocument.Parse(alert.RawJson);
            var hasQueryResult = QueryResultPayload.TryGetQueryResult(document.RootElement, out var queryResult);
            var definition = hasQueryResult && QueryResultPayload.TryGetString(queryResult, "type", out var type) && definitions.TryGetValue(type, out var typedDefinition)
                ? typedDefinition
                : definitions.GetValueOrDefault("*");
            if (definition is null)
            {
                return new AlertDisplayIdentity(fallbackTarget, string.Empty);
            }

            var rows = hasQueryResult && QueryResultPayload.TryReadTable(queryResult, out var table) ? ReadRows(table) : [];
            var target = FirstMeaningfulColumn(rows, definition.Identity.TargetColumns)
                ?? alert.GetDimensionValue(definition.Identity.TargetDimensions.ToArray())
                ?? fallbackTarget;
            var site = FirstMeaningfulColumn(rows, definition.Identity.SiteColumns)
                ?? alert.GetDimensionValue(definition.Identity.SiteDimensions.ToArray())
                ?? string.Empty;

            return new AlertDisplayIdentity(AlertRecord.GetTargetName(target), site);
        }
        catch (JsonException)
        {
            return new AlertDisplayIdentity(fallbackTarget, string.Empty);
        }
    }

    private static IReadOnlyList<QueryResultRowValues> ReadRows(QueryResultTable table) =>
        table.Rows.Select(values => new QueryResultRowValues(values, table.ColumnIndexes)).ToArray();

    private static string? FirstMeaningfulColumn(
        IReadOnlyList<QueryResultRowValues> rows,
        IReadOnlyList<string> columns) => columns
        .SelectMany(column => rows.Select(row => row.Get(column)))
        .FirstOrDefault(AlertRecord.IsMeaningfulTarget);

    /// <summary>
    /// Reads and deserializes one alert-definition file. Property names are matched without regard to letter casing.
    /// </summary>
    private static QueryResultDefinition LoadDefinition(string path) =>
        JsonSerializer.Deserialize<QueryResultDefinition>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? new QueryResultDefinition();

    /// <summary>
    /// Applies the configured summary rules to all query rows. The resulting badges show values such as passed tests, failures, or link counts.
    /// </summary>
    private static IReadOnlyList<QueryResultBadge> BuildSummary(
        IReadOnlyList<QueryResultSummaryRule> rules,
        IReadOnlyList<QueryResultRowValues> rows,
        UiText? text)
    {
        var badges = new List<QueryResultBadge>();
        foreach (var rule in rules)
        {
            var count = rule.Operation.ToLowerInvariant() switch
            {
                "row-count" => rows.Count,
                "sum" => rows.Sum(row => ParseInteger(row.Get(rule.Column))),
                "object-value-count" => rows.Sum(row => GetObjectEntries(row.Get(rule.Column))
                    .Count(entry => Matches(entry.Value, rule.Match, rule.Exclude))),
                _ => 0
            };

            if (count != 0 || !rule.OmitWhenZero)
            {
                badges.Add(new QueryResultBadge(
                    text is not null && !string.IsNullOrWhiteSpace(rule.LabelKey)
                        ? text.Plural(rule.LabelKey, count)
                        : $"{count} {(count == 1 && !string.IsNullOrWhiteSpace(rule.SingularLabel) ? rule.SingularLabel : rule.Label)}",
                    count == 0 && !string.IsNullOrWhiteSpace(rule.ZeroTone) ? rule.ZeroTone : rule.Tone));
            }
        }

        return badges;
    }

    /// <summary>
    /// Converts one query-result row into its title, metadata, alerts, facts, and expandable details. Empty values and hidden fields are left out.
    /// </summary>
    private static QueryResultPresentationRow BuildRow(
        QueryResultRowDefinition definition,
        QueryResultRowValues row,
        UiText? text)
    {
        var metadata = definition.Metadata
            .Select(field => BuildValue(field, row, text))
            .Where(value => !string.IsNullOrWhiteSpace(value.Value))
            .ToArray();
        var facts = definition.Facts
            .Select(field => BuildValue(field, row, text))
            .Where(value => !string.IsNullOrWhiteSpace(value.Value) &&
                (string.IsNullOrWhiteSpace(value.HideWhen) || !value.Value.Equals(value.HideWhen, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var alerts = definition.Alerts
            .SelectMany(rule => BuildItems(rule, row, text))
            .ToArray();
        var details = definition.Details
            .Select(rule => new QueryResultDetails(
                DisplayDetailsLabel(rule, row, text),
                BuildItems(rule, row, text)))
            .Where(group => group.Items.Count > 0)
            .ToArray();

        return new QueryResultPresentationRow(
            DisplayTemplate(definition.Title, definition.TitleKey, definition.TitleArguments, row, 0, text),
            metadata,
            alerts,
            facts,
            details);
    }

    /// <summary>
    /// Reads the column configured for a metadata or fact field. It keeps the display settings with the value for the Razor renderer.
    /// </summary>
    private static QueryResultValue BuildValue(QueryResultFieldDefinition field, QueryResultRowValues row, UiText? text) =>
        new(DisplayLabel(field.Label, field.LabelKey, text), row.Get(field.Column), field.Format, field.Tone, field.HideWhen);

    /// <summary>
    /// Builds alert or detail items from either one column value or the properties of a JSON object. It also applies filtering, ordering, and tone rules from the definition.
    /// </summary>
    private static IReadOnlyList<QueryResultItem> BuildItems(
        QueryResultItemRule rule,
        QueryResultRowValues row,
        UiText? text)
    {
        if (rule.Source.Equals("object-entries", StringComparison.OrdinalIgnoreCase))
        {
            return GetObjectEntries(row.Get(rule.Column))
                .Where(entry => Matches(entry.Value, rule.Match, rule.Exclude))
                .OrderBy(entry => rule.FailureFirst && entry.Value.Equals("Passed", StringComparison.OrdinalIgnoreCase))
                .ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new QueryResultItem(entry.Key, entry.Value, ResolveTone(rule, entry.Value)))
                .ToArray();
        }

        var value = row.Get(rule.Column);
        if (string.IsNullOrWhiteSpace(value) ||
            (!string.IsNullOrWhiteSpace(rule.HideWhen) && value.Equals(rule.HideWhen, StringComparison.OrdinalIgnoreCase)))
        {
            return [];
        }

        return [new QueryResultItem(
            DisplayTemplate(rule.Label, rule.LabelKey, rule.LabelArguments, row, 0, text),
            value, ResolveTone(rule, value))];
    }

    /// <summary>
    /// Selects the visual tone for an item. A value matching the configured success value receives the success tone; otherwise the rule's normal tone is used.
    /// </summary>
    private static string ResolveTone(QueryResultItemRule rule, string value) =>
        !string.IsNullOrWhiteSpace(rule.SuccessValue) && value.Equals(rule.SuccessValue, StringComparison.OrdinalIgnoreCase)
            ? "success"
            : rule.Tone;

    /// <summary>
    /// Checks whether a value satisfies the optional include and exclude filters. Empty filters do not restrict the value.
    /// </summary>
    private static bool Matches(string value, string equals, string notEquals) =>
        (string.IsNullOrWhiteSpace(equals) || value.Equals(equals, StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrWhiteSpace(notEquals) || !value.Equals(notEquals, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Parses a JSON object stored inside a query-result column and returns its properties as name-value pairs. Invalid or non-object JSON produces an empty list.
    /// </summary>
    private static IReadOnlyList<KeyValuePair<string, string>> GetObjectEntries(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.EnumerateObject()
                    .Select(property => KeyValuePair.Create(property.Name, property.Value.ToString()))
                    .ToArray()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Replaces placeholders such as {Computer} with values from the current row and {count} with the supplied count. Missing columns produce an empty replacement.
    /// </summary>
    private static string ExpandTemplate(string template, QueryResultRowValues row, int count = 0) =>
        Regex.Replace(template.Replace("{count}", count.ToString(CultureInfo.InvariantCulture)), "\\{([^{}]+)\\}",
            match => row.Get(match.Groups[1].Value));

    private static string DisplayLabel(string label, string key, UiText? text) =>
        text is not null && !string.IsNullOrWhiteSpace(key) ? text[key] : label;

    private static string DisplayDetailsLabel(QueryResultItemRule rule, QueryResultRowValues row, UiText? text)
    {
        var count = GetObjectEntries(row.Get(rule.Column)).Count;
        return text is not null && !string.IsNullOrWhiteSpace(rule.LabelKey) &&
            rule.Source.Equals("object-entries", StringComparison.OrdinalIgnoreCase)
                ? text.Plural(rule.LabelKey, count)
                : DisplayTemplate(rule.Label, rule.LabelKey, rule.LabelArguments, row, count, text);
    }

    private static string DisplayTemplate(
        string template, string key, IReadOnlyList<string> arguments,
        QueryResultRowValues row, int count, UiText? text) =>
        text is not null && !string.IsNullOrWhiteSpace(key)
            ? text.Format(key, arguments.Select(argument =>
                argument == "count" ? (object)count : row.Get(argument)).ToArray())
            : ExpandTemplate(template, row, count);

    /// <summary>
    /// Parses an integer using culture-independent rules for summary calculations. Invalid values are treated as zero.
    /// </summary>
    private static int ParseInteger(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private sealed record QueryResultRowValues(
        IReadOnlyList<JsonElement> Values,
        IReadOnlyDictionary<string, int> ColumnIndexes)
    {
        /// <summary>
        /// Gets a row value by its Log Analytics column name instead of by array position. Missing columns return an empty string, and non-string JSON values are converted to text.
        /// </summary>
        public string Get(string column) =>
            ColumnIndexes.TryGetValue(column, out var index) && index < Values.Count
                ? Values[index].ValueKind == JsonValueKind.String
                    ? Values[index].GetString() ?? string.Empty
                    : Values[index].ToString()
                : string.Empty;
    }
}

/// <summary>
/// Contains the complete generic model rendered in one Search Result cell. It combines an accessible label, summary badges, and one or more presentation rows.
/// </summary>
public sealed record QueryResultPresentation(
    string Label,
    bool CollapseRows,
    IReadOnlyList<QueryResultBadge> Summary,
    IReadOnlyList<QueryResultPresentationRow> Rows);

/// <summary>
/// Represents one calculated summary badge and its visual tone. Badges are displayed above the detailed query-result rows.
/// </summary>
public sealed record QueryResultBadge(string Text, string Tone);

/// <summary>
/// Represents one normalized query-result row ready for generic rendering. It separates compact metadata, prominent alerts, supporting facts, and expandable details.
/// </summary>
public sealed record QueryResultPresentationRow(
    string Title,
    IReadOnlyList<QueryResultValue> Metadata,
    IReadOnlyList<QueryResultItem> Alerts,
    IReadOnlyList<QueryResultValue> Facts,
    IReadOnlyList<QueryResultDetails> Details);
/// <summary>
/// Carries a labeled field value and its configured display settings. It is used for metadata and supporting facts.
/// </summary>
public sealed record QueryResultValue(string Label, string Value, string Format, string Tone, string HideWhen);

/// <summary>
/// Represents one alert or detail item with a label, value, and visual tone. Items may come from a normal column or an expanded JSON object.
/// </summary>
public sealed record QueryResultItem(string Label, string Value, string Tone);

/// <summary>
/// Groups query-result items under an expandable heading. The Razor renderer displays each group as an HTML details element.
/// </summary>
public sealed record QueryResultDetails(string Label, IReadOnlyList<QueryResultItem> Items);

/// <summary>
/// Contains the descriptor-resolved target and site used consistently throughout the Web UI and query API.
/// </summary>
public sealed record AlertDisplayIdentity(string TargetName, string SiteName);

/// <summary>
/// Models one JSON alert-definition file after deserialization. Its type selects matching payloads and its rules describe the complete presentation.
/// </summary>
public sealed class QueryResultDefinition
{
    public string Type { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string LabelKey { get; set; } = string.Empty;
    public AlertIdentityDefinition Identity { get; set; } = new();
    public bool CollapseRows { get; set; }
    public IReadOnlyList<QueryResultSummaryRule> Summary { get; set; } = [];
    public QueryResultRowDefinition Row { get; set; } = new();
}

/// <summary>
/// Describes how one summary badge is calculated and displayed. Supported operations count rows, sum a column, or count matching values in a JSON object.
/// </summary>
public sealed class QueryResultSummaryRule
{
    public string Operation { get; set; } = string.Empty;
    public string Column { get; set; } = string.Empty;
    public string Match { get; set; } = string.Empty;
    public string Exclude { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string LabelKey { get; set; } = string.Empty;
    public string SingularLabel { get; set; } = string.Empty;
    public string Tone { get; set; } = string.Empty;
    public string ZeroTone { get; set; } = string.Empty;
    public bool OmitWhenZero { get; set; }
}

/// <summary>
/// Describes how every query-result row is divided into title, metadata, alerts, facts, and details. Column placeholders are resolved by the presenter.
/// </summary>
public sealed class QueryResultRowDefinition
{
    public string Title { get; set; } = string.Empty;
    public string TitleKey { get; set; } = string.Empty;
    public IReadOnlyList<string> TitleArguments { get; set; } = [];
    public IReadOnlyList<QueryResultFieldDefinition> Metadata { get; set; } = [];
    public IReadOnlyList<QueryResultItemRule> Alerts { get; set; } = [];
    public IReadOnlyList<QueryResultFieldDefinition> Facts { get; set; } = [];
    public IReadOnlyList<QueryResultItemRule> Details { get; set; } = [];
}

/// <summary>
/// Describes one metadata or fact field read from a named query-result column. It can supply a label, format, tone, and value to hide.
/// </summary>
public sealed class QueryResultFieldDefinition
{
    public string Column { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string LabelKey { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public string Tone { get; set; } = string.Empty;
    public string HideWhen { get; set; } = string.Empty;
}

/// <summary>
/// Describes how alert or detail items are created from a column or JSON-object entries. It controls filtering, ordering, visibility, and visual tone.
/// </summary>
public sealed class QueryResultItemRule
{
    public string Source { get; set; } = string.Empty;
    public string Column { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string LabelKey { get; set; } = string.Empty;
    public IReadOnlyList<string> LabelArguments { get; set; } = [];
    public string Match { get; set; } = string.Empty;
    public string Exclude { get; set; } = string.Empty;
    public string HideWhen { get; set; } = string.Empty;
    public string Tone { get; set; } = string.Empty;
    public string SuccessValue { get; set; } = string.Empty;
    public bool FailureFirst { get; set; }
}

/// <summary>
/// Defines ordered query-result columns and alert dimensions used to resolve an alert's target and site.
/// </summary>
public sealed class AlertIdentityDefinition
{
    public IReadOnlyList<string> TargetColumns { get; set; } = [];
    public IReadOnlyList<string> SiteColumns { get; set; } = [];
    public IReadOnlyList<string> TargetDimensions { get; set; } = [];
    public IReadOnlyList<string> SiteDimensions { get; set; } = [];
}