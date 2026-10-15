namespace MonitoringApp;

/// <summary>
/// Configures the selectable hierarchy layers and initial selections for the alert graph.
/// </summary>
public sealed class AlertGraphOptions
{
    public const string SectionName = "AlertGraph";

    public AlertGraphLayerChoice[] Layer1 { get; init; } = [];

    public AlertGraphLayerChoice[] Layer2 { get; init; } = [];

    public AlertGraphLayerChoice[] Layer3 { get; init; } = [];

    public AlertGraphLayer DefaultLayer1 { get; init; }
    public AlertGraphLayer DefaultLayer2 { get; init; }
    public AlertGraphLayer DefaultLayer3 { get; init; }
    public AlertGraphSiteLocationOptions[] SiteLocations { get; init; } =
        AlertGraphSiteLocationOptions.CreateDefaults();

    public IReadOnlyList<AlertGraphLayerChoice> ChoicesForLevel(int level) => level switch
    {
        1 => Layer1,
        2 => Layer2,
        3 => Layer3,
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };

    public string Label(AlertGraphLayer layer) =>
        Layer1.Concat(Layer2).Concat(Layer3)
            .FirstOrDefault(choice => choice.Value == layer)?.Label ?? layer.ToString();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        ValidateLevel(1, Layer1, DefaultLayer1, errors);
        ValidateLevel(2, Layer2, DefaultLayer2, errors);
        ValidateLevel(3, Layer3, DefaultLayer3, errors);
        ValidateSiteLocations(SiteLocations, errors);
        return errors;
    }

    private static void ValidateLevel(
        int level,
        IReadOnlyList<AlertGraphLayerChoice> choices,
        AlertGraphLayer defaultLayer,
        ICollection<string> errors)
    {
        if (choices.Count == 0)
        {
            errors.Add($"{SectionName}:Layer{level} must contain at least one option.");
            return;
        }

        if (choices.Any(choice => string.IsNullOrWhiteSpace(choice.Label)))
        {
            errors.Add($"{SectionName}:Layer{level} option labels must not be empty.");
        }

        if (choices.Select(choice => choice.Value).Distinct().Count() != choices.Count)
        {
            errors.Add($"{SectionName}:Layer{level} must not contain duplicate values.");
        }

        if (!choices.Any(choice => choice.Value == defaultLayer))
        {
            errors.Add($"{SectionName}:DefaultLayer{level} must be included in Layer{level}.");
        }
    }

    private static void ValidateSiteLocations(
        IReadOnlyList<AlertGraphSiteLocationOptions> locations,
        ICollection<string> errors)
    {
        for (var index = 0; index < locations.Count; index++)
        {
            var location = locations[index];
            var path = $"{SectionName}:SiteLocations:{index}";
            if (string.IsNullOrWhiteSpace(location.DisplayName))
            {
                errors.Add($"{path}:DisplayName must not be empty.");
            }
            if (!double.IsFinite(location.Latitude) || location.Latitude is < -90 or > 90)
            {
                errors.Add($"{path}:Latitude must be between -90 and 90.");
            }
            if (!double.IsFinite(location.Longitude) || location.Longitude is < -180 or > 180)
            {
                errors.Add($"{path}:Longitude must be between -180 and 180.");
            }
            if (!double.IsFinite(location.LabelOffsetX) || !double.IsFinite(location.LabelOffsetY))
            {
                errors.Add($"{path}:Label offsets must be finite numbers.");
            }
            if (location.Aliases.Length == 0 || location.Aliases.Any(string.IsNullOrWhiteSpace))
            {
                errors.Add($"{path}:Aliases must contain at least one non-empty site name.");
            }
            if (location.Aliases
                .Select(alias => alias.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != location.Aliases.Length)
            {
                errors.Add($"{path}:Aliases must not contain duplicates.");
            }
            if (location.TextAnchor is not ("start" or "middle" or "end"))
            {
                errors.Add($"{path}:TextAnchor must be start, middle, or end.");
            }
        }

        if (locations
            .Select(location => location.DisplayName.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != locations.Count)
        {
            errors.Add($"{SectionName}:SiteLocations must not contain duplicate display names.");
        }

        var duplicateAliases = locations
            .SelectMany(location => location.Aliases.Select(alias => alias.Trim()))
            .GroupBy(alias => alias, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (duplicateAliases.Length > 0)
        {
            errors.Add(
                $"{SectionName}:SiteLocations aliases must be unique across locations: " +
                $"{string.Join(", ", duplicateAliases)}.");
        }
    }
}

public sealed class AlertGraphSiteLocationOptions
{
    public string DisplayName { get; init; } = string.Empty;
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public double LabelOffsetX { get; init; } = 30;
    public double LabelOffsetY { get; init; } = -31;
    public string TextAnchor { get; init; } = "start";
    public string[] Aliases { get; init; } = [];

    public static AlertGraphSiteLocationOptions[] CreateDefaults() =>
    [
        new()
        {
            DisplayName = "New York",
            Latitude = 40.7128,
            Longitude = -74.0060,
            LabelOffsetX = -30,
            LabelOffsetY = -31,
            TextAnchor = "end",
            Aliases = ["New York", "NewYork", "NYC"]
        },
        new()
        {
            DisplayName = "Singapore",
            Latitude = 1.3521,
            Longitude = 103.8198,
            Aliases = ["Singapore", "Singapure"]
        },
        new()
        {
            DisplayName = "Frankfurt",
            Latitude = 50.1109,
            Longitude = 8.6821,
            LabelOffsetX = 35,
            LabelOffsetY = 38,
            Aliases = ["Frankfurt"]
        },
        new()
        {
            DisplayName = "London",
            Latitude = 51.5074,
            Longitude = -0.1278,
            LabelOffsetX = -35,
            LabelOffsetY = -31,
            TextAnchor = "end",
            Aliases = ["London"]
        }
    ];
}