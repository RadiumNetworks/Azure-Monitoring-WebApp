using System.Diagnostics;

namespace MonitoringApp.Tests;

public sealed class ApplicationTelemetryTests
{
    [Fact]
    public void StartsNamedInternalOperationWhenSourceIsObserved()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ApplicationTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = ApplicationTelemetry.StartOperation("AlertStore.GetAll");

        Assert.NotNull(activity);
        Assert.Equal("AlertStore.GetAll", activity.OperationName);
        Assert.Equal(ActivityKind.Internal, activity.Kind);
    }
}
