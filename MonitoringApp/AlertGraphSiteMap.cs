namespace MonitoringApp;

public static class AlertGraphSiteMap
{
    public const double Width = 2756;
    public const double Height = 1399;

    private const double CenterLongitude = 10;
    private const double ProjectionScale = 517;
    private static readonly double[] RobinsonX =
    [
        1, .9986, .9954, .99, .9822, .973, .96, .9427, .9216, .8962,
        .8679, .835, .7986, .7597, .7186, .6732, .6213, .5722, .5322
    ];
    private static readonly double[] RobinsonY =
    [
        0, .062, .124, .186, .248, .31, .372, .434, .4958, .5571,
        .6176, .6769, .7346, .7903, .8435, .8936, .9394, .9761, 1
    ];
    public static IReadOnlyList<AlertGraphSiteMarker> Build(
        IEnumerable<AlertGraphRecord> records,
        IReadOnlyList<AlertGraphSiteLocationOptions> locations,
        DateTimeOffset timeUtc)
    {
        var snapshot = records as IReadOnlyCollection<AlertGraphRecord> ?? records.ToArray();
        var activeIds = AlertGraphActiveAlerts.ResolveIds(snapshot);

        return locations
            .Select(location =>
            {
                var openAlertCount = snapshot.Count(record =>
                    activeIds.Contains(record.Id) &&
                    location.Aliases.Contains(record.Site.Trim(), StringComparer.OrdinalIgnoreCase));
                var (x, y) = Project(location.Latitude, location.Longitude);
                return new AlertGraphSiteMarker(
                    location.DisplayName,
                    x,
                    y,
                    location.LabelOffsetX,
                    location.LabelOffsetY,
                    location.TextAnchor,
                    openAlertCount,
                    AlertGraphSolarOverlay.ResolvePhase(
                        timeUtc,
                        location.Latitude,
                        location.Longitude));
            })
            .ToArray();
    }

    internal static (double X, double Y) Project(double latitude, double longitude)
    {
        var boundedLatitude = Math.Clamp(latitude, -90, 90);
        var relativeLongitude = longitude - CenterLongitude;
        while (relativeLongitude < -180)
        {
            relativeLongitude += 360;
        }
        while (relativeLongitude > 180)
        {
            relativeLongitude -= 360;
        }

        var absoluteLatitude = Math.Abs(boundedLatitude);
        var lowerIndex = Math.Min((int)(absoluteLatitude / 5), RobinsonX.Length - 2);
        var fraction = (absoluteLatitude - lowerIndex * 5) / 5;
        var xCoefficient = Interpolate(RobinsonX[lowerIndex], RobinsonX[lowerIndex + 1], fraction);
        var yCoefficient = Interpolate(RobinsonY[lowerIndex], RobinsonY[lowerIndex + 1], fraction);
        var longitudeRadians = relativeLongitude * Math.PI / 180;
        var x = Width / 2 + ProjectionScale * .8487 * longitudeRadians * xCoefficient;
        var y = Height / 2 - Math.Sign(boundedLatitude) * ProjectionScale * 1.3523 * yCoefficient;
        return (x, y);
    }

    private static double Interpolate(double start, double end, double fraction) =>
        start + (end - start) * fraction;
}

public sealed record AlertGraphSiteMarker(
    string Name,
    double X,
    double Y,
    double LabelOffsetX,
    double LabelOffsetY,
    string TextAnchor,
    int OpenAlertCount,
    AlertGraphSolarPhase SolarPhase)
{
    public bool HasOpenAlerts => OpenAlertCount > 0;
    public string StatusLabel => HasOpenAlerts
        ? $"{OpenAlertCount} open alert{(OpenAlertCount == 1 ? string.Empty : "s")}"
        : "No open alerts";
    public string SolarPhaseLabel => SolarPhase switch
    {
        AlertGraphSolarPhase.Day => "Daylight",
        AlertGraphSolarPhase.Twilight => "Twilight",
        _ => "Night"
    };
}
