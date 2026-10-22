using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MonitoringApp;

public sealed class AnalysisVisualizationOptions
{
    public string? Color { get; set; }
    public string NullColor { get; set; } = "gray";
    public string NullText { get; set; } = "Unknown";
    public string CellDisplay { get; set; } = "Text";
    public bool WrapText { get; set; } = true;
    public int? Decimals { get; set; }
    public int? StatTitleFontSize { get; set; }
    public int? StatValueFontSize { get; set; }
    public double? Minimum { get; set; }
    public double? Maximum { get; set; }
    public List<AnalysisColorThreshold> Thresholds { get; set; } = [];
    public List<AnalysisValueMapping> ValueMappings { get; set; } = [];
    public List<AnalysisColumnDisplay> Columns { get; set; } = [];
}

public sealed class AnalysisColumnDisplay
{
    public string Name { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public int? Width { get; set; }
    public bool Hidden { get; set; }
    public bool? WrapText { get; set; }
    public bool BreakAtFirstDot { get; set; }
    public int? Decimals { get; set; }
    public string? Unit { get; set; }
    public string? Color { get; set; }
    public string? CellDisplay { get; set; }
    public string? NullText { get; set; }
    public List<AnalysisColorThreshold> Thresholds { get; set; } = [];
    public List<AnalysisValueMapping> ValueMappings { get; set; } = [];
}

public sealed record AnalysisColorThreshold(double Value, string Color);
public sealed record AnalysisValueMapping(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Value,
    string? Text = null, string? Color = null);

public static partial class AnalysisVisualization
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly IReadOnlyDictionary<string, string> Colors = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["green"] = "#176b40", ["red"] = "#a12424", ["yellow"] = "#805500",
        ["orange"] = "#a44912", ["blue"] = "#245b91", ["purple"] = "#7040a0",
        ["gray"] = "#53616d", ["teal"] = "#157a6e"
    };

    [GeneratedRegex("^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColor();

    public static string Serialize(AnalysisVisualizationOptions? options) =>
        JsonSerializer.Serialize(options ?? new(), JsonOptions);

    public static string FormatForEditing(string json)
    {
        using var document = JsonDocument.Parse(json);
        var formatted = JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        if (formatted.Length > 65536)
            throw new InvalidOperationException("Indented visualization JSON exceeds the editor limit of 65536 characters.");
        return formatted;
    }

    public static AnalysisVisualizationOptions Parse(string json)
    {
        if (json is null || json.Length > 65536)
            throw new InvalidOperationException("Visualization JSON is required and may contain at most 65536 characters.");
        AnalysisVisualizationOptions options;
        try
        {
            options = JsonSerializer.Deserialize<AnalysisVisualizationOptions>(json, JsonOptions)
                ?? throw new InvalidOperationException("Visualization JSON must be an object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Invalid visualization JSON: {exception.Message}", exception);
        }
        Validate(options);
        return options;
    }

    public static void Validate(AnalysisVisualizationOptions options)
    {
        CheckColor(options.Color);
        CheckColor(options.NullColor);
        CheckDisplay(options.CellDisplay);
        CheckDecimals(options.Decimals);
        if (options.StatTitleFontSize is < 8 or > 72 || options.StatValueFontSize is < 8 or > 72)
            throw new InvalidOperationException("Stat title and value font sizes must be between 8 and 72 pixels.");
        if (options.NullColor is null || options.CellDisplay is null || options.NullText is null || options.NullText.Length > 256 ||
            options.Minimum is double min && !double.IsFinite(min) ||
            options.Maximum is double max && !double.IsFinite(max) ||
            options.Maximum.HasValue && options.Maximum <= (options.Minimum ?? 0))
            throw new InvalidOperationException("Visualization bounds must be finite and increasing; null text is limited to 256 characters.");
        CheckRules(options.Thresholds, options.ValueMappings);
        if (options.Columns is null || options.Columns.Count > 100 ||
            options.Columns.Any(column => column is null || string.IsNullOrWhiteSpace(column.Name) || column.Name.Length > 256) ||
            options.Columns.DistinctBy(column => column.Name).Count() != options.Columns.Count)
            throw new InvalidOperationException("Visualization columns require unique, nonempty names (maximum 100 columns).");
        foreach (var column in options.Columns)
        {
            CheckColor(column.Color);
            CheckDisplay(column.CellDisplay);
            CheckDecimals(column.Decimals);
            CheckRules(column.Thresholds, column.ValueMappings);
            if (column.Width is < 50 or > 1000 ||
                column.DisplayName?.Length > 256 || column.Unit?.Length > 64 || column.NullText?.Length > 256)
                throw new InvalidOperationException("Column width must be 50 to 1000 pixels; labels and null text are limited to 256 characters and units to 64.");
        }
    }

    private static void CheckRules(List<AnalysisColorThreshold> thresholds, List<AnalysisValueMapping> mappings)
    {
        if (thresholds is null || mappings is null || thresholds.Count > 50 || mappings.Count > 100)
            throw new InvalidOperationException("Specify arrays with at most 50 thresholds and 100 value mappings.");
        double? previous = null;
        foreach (var threshold in thresholds)
        {
            if (threshold is null || !double.IsFinite(threshold.Value) ||
                previous.HasValue && threshold.Value <= previous.Value || string.IsNullOrWhiteSpace(threshold.Color))
                throw new InvalidOperationException("Threshold values must be finite, unique and strictly increasing, with a color.");
            CheckColor(threshold.Color);
            previous = threshold.Value;
        }
        if (mappings.Any(mapping => mapping is null || mapping.Value?.Length > 256 || mapping.Text?.Length > 256) ||
            mappings.Select(mapping => mapping.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != mappings.Count)
            throw new InvalidOperationException("Value mappings must be unique (case-insensitive) and limited to 256 characters per value/text.");
        foreach (var mapping in mappings)
            CheckColor(mapping.Color);
    }

    private static void CheckColor(string? color)
    {
        if (color is not null && !Colors.ContainsKey(color) && !HexColor().IsMatch(color))
            throw new InvalidOperationException("Colors must be green, red, yellow, orange, blue, purple, gray, teal, or #RRGGBB.");
    }

    private static void CheckDisplay(string? display)
    {
        if (display is not null && display is not ("Text" or "ColorText" or "ColorBackground"))
            throw new InvalidOperationException("Cell display must be Text, ColorText or ColorBackground.");
    }

    private static void CheckDecimals(int? decimals)
    {
        if (decimals is < 0 or > 6)
            throw new InvalidOperationException("Decimals must be between 0 and 6.");
    }

    public static AnalysisColumnDisplay? Column(AnalysisVisualizationOptions options, string name) =>
        options.Columns.FirstOrDefault(column => column.Name == name);

    public static string? Color(AnalysisVisualizationOptions options, string? value, string field)
    {
        var column = Column(options, field);
        var mappings = column?.ValueMappings.Count > 0 ? column.ValueMappings : options.ValueMappings;
        var mapping = mappings.FirstOrDefault(item => string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase));
        var color = mapping?.Color;
        if (color is null && value is null)
            color = options.NullColor;
        if (color is null)
        {
            color = column?.Color ?? options.Color;
            var thresholds = column?.Thresholds.Count > 0 ? column.Thresholds : options.Thresholds;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
                color = thresholds.LastOrDefault(step => number >= step.Value)?.Color ?? color;
        }
        return color is null ? null : Colors.GetValueOrDefault(color, color);
    }

    public static string Text(AnalysisVisualizationOptions options, string? value, string field, string? numericFormat = null)
    {
        var column = Column(options, field);
        var mappings = column?.ValueMappings.Count > 0 ? column.ValueMappings : options.ValueMappings;
        var mapped = mappings.FirstOrDefault(item => string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase));
        if (mapped?.Text is not null)
            return mapped.Text;
        if (value is null)
            return column?.NullText ?? options.NullText;
        if (column?.Unit is "datetime" or "date" &&
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
            return timestamp.ToUniversalTime().ToString(column.Unit == "date" ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        var decimals = column?.Decimals ?? options.Decimals;
        return (decimals.HasValue || numericFormat is not null) &&
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
                ? number.ToString(decimals.HasValue ? $"F{decimals.Value}" : numericFormat, CultureInfo.CurrentCulture) : value;
    }

    public static string TableText(AnalysisVisualizationOptions options, string? value, string field)
    {
        var text = Text(options, value, field);
        var separator = text.IndexOf('.');
        return value is not null && Column(options, field)?.BreakAtFirstDot == true && separator >= 0
            ? string.Concat(text.AsSpan(0, separator), "\n", text.AsSpan(separator + 1))
            : text;
    }

    public static string? Unit(AnalysisVisualizationOptions options, string field, string? fallback = null) =>
        Column(options, field)?.Unit is "datetime" or "date" ? null : Column(options, field)?.Unit ?? fallback;

    public static string CellStyle(AnalysisVisualizationOptions options, string? value, string field)
    {
        var column = Column(options, field);
        var color = Color(options, value, field);
        var display = column?.CellDisplay ?? options.CellDisplay;
        var wrap = column?.WrapText ?? options.WrapText;
        var whitespace = column?.BreakAtFirstDot == true
            ? (wrap ? "pre-line" : "pre")
            : (wrap ? "normal" : "nowrap");
        var style = $"white-space:{whitespace};";
        if (column?.Width is int width)
            style += $"min-width:{width}px;max-width:{width}px;";
        if (color is not null && display != "Text")
            style += display == "ColorBackground"
                ? $"background-color:{color};color:{Foreground(color)};"
                : $"color:{color};font-weight:600;";
        return style;
    }

    public static string Foreground(string color)
    {
        var red = int.Parse(color.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var green = int.Parse(color.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var blue = int.Parse(color.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return red * 0.299 + green * 0.587 + blue * 0.114 > 150 ? "#17211e" : "#ffffff";
    }
}
