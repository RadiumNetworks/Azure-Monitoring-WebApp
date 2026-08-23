namespace MonitoringApp;

public sealed record AlertTimeZoneOption(string Id, string Label, TimeZoneInfo TimeZone);

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

    public static AlertTimeZoneOption Get(string id) =>
        Options.FirstOrDefault(option => option.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? Options[0];

    public static DateTime ToDisplayTime(DateTimeOffset timestamp, string timeZoneId) =>
        TimeZoneInfo.ConvertTime(timestamp, Get(timeZoneId).TimeZone).DateTime;

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

    public static string FormatWallClock(DateTime? value) =>
        value?.ToString(WallClockFormat, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

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