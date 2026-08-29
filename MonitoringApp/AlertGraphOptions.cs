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
}