namespace MonitoringApp;

/// <summary>
/// Represents an immutable comment written to the operational logbook.
/// </summary>
public sealed class LogbookEntry
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string User { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
}
