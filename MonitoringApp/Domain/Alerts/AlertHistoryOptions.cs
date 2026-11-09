namespace MonitoringApp;

/// <summary>
/// Configures the recent alert-history window used by Inbox navigation and the alert graph.
/// </summary>
public sealed class AlertHistoryOptions
{
    public const string SectionName = "AlertHistory";

    public int Days { get; init; } = 7;

    public DateTimeOffset GetCutoff(DateTimeOffset now) => now.AddDays(-Days);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Days is < 1 or > 3650)
        {
            errors.Add($"{SectionName}:Days must be between 1 and 3650.");
        }

        return errors;
    }
}