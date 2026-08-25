namespace MonitoringApp;

/// <summary>
/// Describes one time-zone choice available in the Alert Console. The identifier is used by UI state, while the label and TimeZone support display conversion.
/// </summary>
public sealed record AlertTimeZoneOption(string Id, string Label, TimeZoneInfo TimeZone);

/// <summary>
/// Provides the supported time zones and wall-clock conversion helpers. It handles both IANA and Windows time-zone identifiers.
/// </summary>
public static class AlertTimeZones
{
    public const string WallClockFormat = "yyyy-MM-ddTHH:mm";
    private static readonly string[] WallClockInputFormats =
    [
        WallClockFormat,
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFF"
    ];

    public static IReadOnlyList<AlertTimeZoneOption> Options { get; } =
    [
        new("utc", "UTC", TimeZoneInfo.Utc),
        new("germany", "Germany", Find("Europe/Berlin", "W. Europe Standard Time")),
        new("singapore", "Singapore", Find("Asia/Singapore", "Singapore Standard Time")),
        new("new-york", "New York", Find("America/New_York", "Eastern Standard Time"))
    ];

    /// <summary>
    /// Finds a configured time-zone option by identifier. Unknown identifiers fall back to UTC.
    /// </summary>
    public static AlertTimeZoneOption Get(string id) =>
        Options.FirstOrDefault(option => option.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? Options[0];

    /// <summary>
    /// Converts an absolute timestamp into wall-clock time in the selected zone. The returned DateTime is intended for display rather than storage.
    /// </summary>
    public static DateTime ToDisplayTime(DateTimeOffset timestamp, string timeZoneId) =>
        TimeZoneInfo.ConvertTime(timestamp, Get(timeZoneId).TimeZone).DateTime;

    /// <summary>
    /// Reinterprets a wall-clock value in one time zone and converts it to another. Invalid daylight-saving times are returned unchanged.
    /// </summary>
    public static DateTime ConvertWallClock(DateTime value, string sourceTimeZoneId, string targetTimeZoneId)
    {
        var source = Get(sourceTimeZoneId).TimeZone;
        var target = Get(targetTimeZoneId).TimeZone;
        var unspecifiedValue = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        if (source.IsInvalidTime(unspecifiedValue))
        {
            return value;
        }

        var utcValue = TimeZoneInfo.ConvertTimeToUtc(unspecifiedValue, source);
        return TimeZoneInfo.ConvertTimeFromUtc(utcValue, target);
    }

    /// <summary>
    /// Formats a nullable wall-clock value for an HTML datetime-local input. Null values become an empty string.
    /// </summary>
    public static string FormatWallClock(DateTime? value) =>
        value?.ToString(WallClockFormat, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>
    /// Parses a datetime-local value into an unspecified-kind DateTime. Unsupported or invalid input returns null.
    /// </summary>
    public static DateTime? ParseWallClock(object? value)
    {
        if (value is DateTime dateTime)
        {
            return DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified);
        }

        return DateTime.TryParseExact(
            value as string,
            WallClockInputFormats,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out var result)
            ? result
            : null;
    }

    /// <summary>
    /// Resolves a time zone using its IANA identifier and falls back to its Windows identifier. This allows the same configuration to run on Linux and Windows.
    /// </summary>
    private static TimeZoneInfo Find(string ianaId, string windowsId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ianaId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
        }
    }
}