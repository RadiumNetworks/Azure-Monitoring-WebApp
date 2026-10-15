using System.Diagnostics;

namespace MonitoringApp;

public static class ApplicationTelemetry
{
    public const string ActivitySourceName = "MonitoringApp";
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public static Activity? StartOperation(string name) =>
        ActivitySource.StartActivity(name, ActivityKind.Internal);
}
