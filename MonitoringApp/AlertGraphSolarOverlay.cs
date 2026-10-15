using System.Globalization;
using System.Text;

namespace MonitoringApp;

public static class AlertGraphSolarOverlay
{
    private const double LatitudeStep = .25;
    private const double LongitudeSearchStep = .5;
    private const double TwilightAltitude = -6;
    private const double MapCenterLongitude = 10;

    public static AlertGraphSolarOverlayResult Build(DateTimeOffset timeUtc)
    {
        var position = CalculatePosition(timeUtc);
        var paths = new List<AlertGraphSolarPath>();
        var firstLongitude = MapCenterLongitude - 180;
        var longitudeSegmentCount = (int)(360 / LongitudeSearchStep);

        for (var latitude = -90d; latitude < 90; latitude += LatitudeStep)
        {
            var sampleLatitude = latitude + LatitudeStep / 2;
            var phases = Enumerable.Range(0, longitudeSegmentCount)
                .Select(index => ResolvePhase(
                    position,
                    sampleLatitude,
                    firstLongitude + (index + .5) * LongitudeSearchStep))
                .ToArray();

            var startIndex = 0;
            while (startIndex < phases.Length)
            {
                var phase = phases[startIndex];
                var endIndex = startIndex + 1;
                while (endIndex < phases.Length && phases[endIndex] == phase)
                {
                    endIndex++;
                }

                if (phase != AlertGraphSolarPhase.Day)
                {
                    paths.Add(new AlertGraphSolarPath(
                        phase,
                        BuildSegmentPath(
                            latitude,
                            latitude + LatitudeStep,
                            firstLongitude + startIndex * LongitudeSearchStep,
                            firstLongitude + endIndex * LongitudeSearchStep)));
                }

                startIndex = endIndex;
            }
        }

        return new AlertGraphSolarOverlayResult(
            position.Declination,
            position.SubsolarLongitude,
            paths);
    }

    public static AlertGraphSolarPhase ResolvePhase(
        DateTimeOffset timeUtc,
        double latitude,
        double longitude) =>
        ResolvePhase(CalculatePosition(timeUtc), latitude, longitude);

    internal static AlertGraphSolarPosition CalculatePosition(DateTimeOffset timeUtc)
    {
        var julianDay = timeUtc.ToUniversalTime().ToUnixTimeMilliseconds() / 86400000d + 2440587.5;
        var daysSinceEpoch = julianDay - 2451545;
        var meanLongitude = NormalizeDegrees(280.46 + .9856474 * daysSinceEpoch);
        var meanAnomaly = DegreesToRadians(NormalizeDegrees(357.528 + .9856003 * daysSinceEpoch));
        var eclipticLongitude = DegreesToRadians(NormalizeDegrees(
            meanLongitude +
            1.915 * Math.Sin(meanAnomaly) +
            .02 * Math.Sin(2 * meanAnomaly)));
        var obliquity = DegreesToRadians(23.439 - .0000004 * daysSinceEpoch);
        var rightAscension = Math.Atan2(
            Math.Cos(obliquity) * Math.Sin(eclipticLongitude),
            Math.Cos(eclipticLongitude));
        var declination = Math.Asin(Math.Sin(obliquity) * Math.Sin(eclipticLongitude));
        var greenwichSiderealTime = NormalizeDegrees(
            280.46061837 + 360.98564736629 * daysSinceEpoch);
        var subsolarLongitude = NormalizeSignedDegrees(
            RadiansToDegrees(rightAscension) - greenwichSiderealTime);
        return new AlertGraphSolarPosition(
            RadiansToDegrees(declination),
            subsolarLongitude);
    }

    private static AlertGraphSolarPhase ResolvePhase(
        AlertGraphSolarPosition position,
        double latitude,
        double longitude)
    {
        var latitudeRadians = DegreesToRadians(latitude);
        var declinationRadians = DegreesToRadians(position.Declination);
        var hourAngle = DegreesToRadians(NormalizeSignedDegrees(
            longitude - position.SubsolarLongitude));
        var sineAltitude =
            Math.Sin(latitudeRadians) * Math.Sin(declinationRadians) +
            Math.Cos(latitudeRadians) * Math.Cos(declinationRadians) * Math.Cos(hourAngle);
        var altitude = RadiansToDegrees(Math.Asin(Math.Clamp(sineAltitude, -1, 1)));
        return altitude >= 0
            ? AlertGraphSolarPhase.Day
            : altitude >= TwilightAltitude
                ? AlertGraphSolarPhase.Twilight
                : AlertGraphSolarPhase.Night;
    }

    private static string BuildSegmentPath(
        double lowerLatitude,
        double upperLatitude,
        double startLongitude,
        double endLongitude)
    {
        var path = new StringBuilder();
        AppendPoint(path, lowerLatitude, startLongitude, true);
        AppendPoint(path, lowerLatitude, endLongitude, false);
        AppendPoint(path, upperLatitude, endLongitude, false);
        AppendPoint(path, upperLatitude, startLongitude, false);
        path.Append(" Z");
        return path.ToString();
    }

    private static void AppendPoint(
        StringBuilder path,
        double latitude,
        double longitude,
        bool first)
    {
        var (x, y) = AlertGraphSiteMap.Project(latitude, longitude);
        path.Append(first ? "M " : " L ");
        path.Append(x.ToString("0.##", CultureInfo.InvariantCulture));
        path.Append(' ');
        path.Append(y.ToString("0.##", CultureInfo.InvariantCulture));
    }

    private static double NormalizeDegrees(double value)
    {
        var normalized = value % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    private static double NormalizeSignedDegrees(double value)
    {
        var normalized = NormalizeDegrees(value);
        return normalized > 180 ? normalized - 360 : normalized;
    }

    private static double DegreesToRadians(double value) => value * Math.PI / 180;
    private static double RadiansToDegrees(double value) => value * 180 / Math.PI;
}

public sealed record AlertGraphSolarOverlayResult(
    double SubsolarLatitude,
    double SubsolarLongitude,
    IReadOnlyList<AlertGraphSolarPath> Paths);

public sealed record AlertGraphSolarPath(
    AlertGraphSolarPhase Phase,
    string Data);

public sealed record AlertGraphSolarPosition(
    double Declination,
    double SubsolarLongitude);

public enum AlertGraphSolarPhase
{
    Day,
    Twilight,
    Night
}
