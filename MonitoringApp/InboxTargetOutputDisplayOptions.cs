namespace MonitoringApp;

public sealed class InboxTargetOutputDisplayOptions
{
    public const string SectionName = "InboxTargetOutputDisplay";

    private static readonly IReadOnlyDictionary<string, InboxTargetColor> Colors =
        new Dictionary<string, InboxTargetColor>(StringComparer.OrdinalIgnoreCase)
        {
            ["green"] = InboxTargetColor.Green,
            ["yellow"] = InboxTargetColor.Yellow,
            ["orange"] = InboxTargetColor.Orange,
            ["red"] = InboxTargetColor.Red,
            ["gray"] = InboxTargetColor.Gray
        };

    public InboxTargetCountColor[] CountColors { get; init; } =
    [
        new() { MinimumCount = 1, Color = "yellow" },
        new() { MinimumCount = 2, Color = "orange" },
        new() { MinimumCount = 3, Color = "red" }
    ];
    public string SuppressedColor { get; init; } = "green";
    public bool CriticalOutageAlwaysRed { get; init; } = true;
    public bool BoldOutageNames { get; init; } = true;
    public bool ShowDomain { get; init; } = true;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!IsSupportedColor(SuppressedColor))
        {
            errors.Add($"{SectionName}:SuppressedColor must be green, yellow, orange, red, or gray.");
        }
        if (CountColors is null || CountColors.Length == 0)
        {
            errors.Add($"{SectionName}:CountColors must contain at least one color band.");
            return errors;
        }
        foreach (var band in CountColors)
        {
            if (band is null)
            {
                errors.Add($"{SectionName}:CountColors must not contain null entries.");
                continue;
            }
            if (band.MinimumCount < 1)
            {
                errors.Add($"{SectionName}:MinimumCount must be at least 1.");
            }
            if (!IsSupportedColor(band.Color))
            {
                errors.Add($"{SectionName}: Color '{band.Color}' is invalid. Use green, yellow, orange, red, or gray.");
            }
        }
        var counts = CountColors.Where(band => band is not null).Select(band => band.MinimumCount).ToArray();
        if (!counts.Contains(1))
        {
            errors.Add($"{SectionName}:CountColors must include MinimumCount 1.");
        }
        if (counts.Distinct().Count() != counts.Length)
        {
            errors.Add($"{SectionName}:CountColors must not contain duplicate MinimumCount values.");
        }
        return errors;
    }

    public InboxTargetColor Color(InboxTargetOutputCell cell) =>
        cell.HasServiceHealthAlert && cell.Count == 0 ? InboxTargetColor.Purple
        : cell.MissingHeartbeat ? InboxTargetColor.MissingHeartbeat
        : CriticalOutageAlwaysRed && cell.IsCriticalOutage ? InboxTargetColor.Red
        : cell.SuppressedOnly ? Colors[SuppressedColor]
        : Colors[CountColors.OrderByDescending(band => band.MinimumCount)
            .First(band => band.MinimumCount <= Math.Max(1, cell.NonSuppressedCount)).Color];

    public string CssClass(InboxTargetOutputCell cell) => "heatmap-cell" +
        (cell.MissingHeartbeat ? " heatmap-cell-missing-heartbeat" : string.Empty) +
        (cell.HasServiceHealthAlert && cell.Count == 0 ? " heatmap-cell-service-health-alert" : string.Empty) +
        (BoldOutageNames && cell.IsOutage ? " heatmap-cell-outage" : string.Empty) +
        (!ShowDomain ? " heatmap-cell-no-domain" : string.Empty);

    public string Style(InboxTargetOutputCell cell) => ColorStyle(Color(cell));

    public IReadOnlyList<InboxTargetColorLegend> Legend()
    {
        var bands = CountColors.OrderBy(band => band.MinimumCount).ToArray();
        var entries = new List<InboxTargetColorLegend>
        {
            new("Suppressed", Colors[SuppressedColor])
        };
        for (var index = 0; index < bands.Length; index++)
        {
            var minimum = bands[index].MinimumCount;
            var maximum = index + 1 < bands.Length ? bands[index + 1].MinimumCount - 1 : (int?)null;
            var label = maximum is null ? $"{minimum}+"
                : maximum == minimum ? $"{minimum}"
                : $"{minimum}-{maximum}";
            entries.Add(new InboxTargetColorLegend(label, Colors[bands[index].Color]));
        }
        if (CriticalOutageAlwaysRed)
        {
            entries.Add(new InboxTargetColorLegend("Critical outage", InboxTargetColor.Red));
        }
        entries.Add(new InboxTargetColorLegend("Service health alert", InboxTargetColor.Purple));
        return entries;
    }

    public static string ColorStyle(InboxTargetColor color) => color switch
    {
        InboxTargetColor.Green => "--heat-hue:112;--heat-saturation:58%;--heat-lightness:84%",
        InboxTargetColor.Yellow => "--heat-hue:48;--heat-saturation:64%;--heat-lightness:70%",
        InboxTargetColor.Orange => "--heat-hue:28;--heat-saturation:78%;--heat-lightness:70%",
        InboxTargetColor.Red => "--heat-hue:0;--heat-saturation:70%;--heat-lightness:58%",
        InboxTargetColor.Purple => "--heat-hue:282;--heat-saturation:52%;--heat-lightness:72%",
        InboxTargetColor.Gray => "--heat-hue:0;--heat-saturation:0%;--heat-lightness:88%",
        InboxTargetColor.MissingHeartbeat => string.Empty,
        _ => throw new ArgumentOutOfRangeException(nameof(color))
    };

    private static bool IsSupportedColor(string? color) =>
        !string.IsNullOrWhiteSpace(color) && Colors.ContainsKey(color);
}

public sealed class InboxTargetCountColor
{
    public int MinimumCount { get; init; }
    public string Color { get; init; } = string.Empty;
}

public sealed record InboxTargetColorLegend(string Label, InboxTargetColor Color);
