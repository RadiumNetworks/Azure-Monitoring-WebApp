namespace MonitoringApp;

public sealed class RcaDisplayOptions
{
    public const string SectionName = "RcaDisplay";

    private static readonly IReadOnlyDictionary<string, string> Colors =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["red"] = "#b42318",
            ["green"] = "#137547",
            ["orange"] = "#a64b00",
            ["yellow"] = "#8a6500",
            ["gray"] = "#58645e"
        };

    public string OpenUnassignedColor { get; init; } = "red";
    public string ClosedColor { get; init; } = "green";
    public string OtherColor { get; init; } = "orange";

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        foreach (var (name, color) in new[]
        {
            (nameof(OpenUnassignedColor), OpenUnassignedColor),
            (nameof(ClosedColor), ClosedColor),
            (nameof(OtherColor), OtherColor)
        })
        {
            if (string.IsNullOrWhiteSpace(color) || !Colors.ContainsKey(color))
                errors.Add($"{SectionName}:{name} must be red, green, orange, yellow, or gray.");
        }
        return errors;
    }

    public string Style(RootCauseAnalysis rca)
    {
        var color = rca.Status == RcaStatuses.Closed ? ClosedColor
            : rca.Status == RcaStatuses.Open && string.IsNullOrWhiteSpace(rca.AssignedTo)
                ? OpenUnassignedColor : OtherColor;
        return $"--rca-color:{Colors[color]}";
    }
}
