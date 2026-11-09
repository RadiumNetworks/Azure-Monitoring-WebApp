using System.Text.RegularExpressions;

namespace MonitoringApp;

internal static class AlertResolutionMarker
{
    public static bool IsPresent(string comments) =>
        Regex.IsMatch(comments, @"\bResolved\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
