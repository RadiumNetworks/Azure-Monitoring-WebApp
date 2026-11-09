using System.Globalization;

namespace MonitoringApp;

public static class AnalysisResultFormatting
{
    public static string Status(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "ok" or "success" or "passed" or "running" => "ok",
        "warning" or "start pending" => "warning",
        "error" or "failed" or "stopped" => "error",
        _ => "unknown"
    };

    public static string StatusSymbol(string status) => status switch
    {
        "ok" => "\u2713", "warning" => "\u26a0", "error" => "\u2715", _ => "?"
    };

    public static string LatencyColor(double value)
    {
        var fraction = Math.Clamp(value / 2000, 0, 1);
        return $"hsl({(120 * (1 - fraction)).ToString("0.##", CultureInfo.InvariantCulture)} 65% 38%)";
    }
}
