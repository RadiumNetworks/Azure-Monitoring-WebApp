namespace MonitoringApp.Tests;

public sealed class LogAnalyticsConnectionDiagnosticsTests
{
    [Fact]
    public async Task DisabledFeatureDoesNotCallExternalAdapters()
    {
        var secret = new FakeSecretProvider();
        var probe = new FakeWorkspaceProbe();
        var diagnostics = new LogAnalyticsConnectionDiagnostics(
            new CrossTenantLogAnalyticsOptions(),
            secret,
            probe);

        var report = await diagnostics.RunAsync();

        Assert.Equal(DiagnosticStatus.Disabled, report.KeyVault.Status);
        Assert.Empty(report.Workspaces);
        Assert.Equal(0, secret.CallCount);
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public async Task SecretFailureMarksAllWorkspaceStepsNotRun()
    {
        var options = CrossTenantLogAnalyticsOptionsTests.CreateValid(
        [
            CrossTenantLogAnalyticsOptionsTests.Workspace(
                "first",
                "11111111-1111-1111-1111-111111111111",
                "first.example"),
            CrossTenantLogAnalyticsOptionsTests.Workspace(
                "second",
                "22222222-2222-2222-2222-222222222222",
                "second.example")
        ]);
        var secret = new FakeSecretProvider
        {
            Exception = new InvalidOperationException("The configured secret is empty.")
        };
        var probe = new FakeWorkspaceProbe();
        var diagnostics = new LogAnalyticsConnectionDiagnostics(options, secret, probe);

        var report = await diagnostics.RunAsync();

        Assert.Equal(DiagnosticStatus.Failed, report.KeyVault.Status);
        Assert.All(report.Workspaces, workspace =>
        {
            Assert.Equal(DiagnosticStatus.NotRun, workspace.Token.Status);
            Assert.Equal(DiagnosticStatus.NotRun, workspace.Query.Status);
        });
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public async Task SuccessfulSecretRunsEveryWorkspaceProbe()
    {
        var options = CrossTenantLogAnalyticsOptionsTests.CreateValid(
        [
            CrossTenantLogAnalyticsOptionsTests.Workspace(
                "first",
                "11111111-1111-1111-1111-111111111111",
                "first.example"),
            CrossTenantLogAnalyticsOptionsTests.Workspace(
                "second",
                "22222222-2222-2222-2222-222222222222",
                "second.example")
        ]);
        var secret = new FakeSecretProvider();
        var probe = new FakeWorkspaceProbe();
        var diagnostics = new LogAnalyticsConnectionDiagnostics(options, secret, probe);

        var report = await diagnostics.RunAsync();

        Assert.Equal(DiagnosticStatus.Succeeded, report.KeyVault.Status);
        Assert.Equal(2, report.Workspaces.Count);
        Assert.All(report.Workspaces, workspace =>
        {
            Assert.Equal(DiagnosticStatus.Succeeded, workspace.Token.Status);
            Assert.Equal(DiagnosticStatus.Succeeded, workspace.Query.Status);
        });
        Assert.Equal(1, secret.CallCount);
        Assert.Equal(2, probe.CallCount);
    }

    private sealed class FakeSecretProvider : ILogAnalyticsSecretProvider
    {
        public int CallCount { get; private set; }
        public Exception? Exception { get; init; }

        public Task<string> GetClientSecretAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return Exception is null
                ? Task.FromResult("test-secret")
                : Task.FromException<string>(Exception);
        }
    }

    private sealed class FakeWorkspaceProbe : ILogAnalyticsWorkspaceProbe
    {
        public int CallCount { get; private set; }

        public Task<LogAnalyticsWorkspaceProbeResult> ProbeAsync(
            CrossTenantLogAnalyticsWorkspaceOptions workspace,
            string applicationClientId,
            string clientSecret,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new LogAnalyticsWorkspaceProbeResult(
                DiagnosticStep.Succeeded("Tenant token", TimeSpan.FromMilliseconds(1)),
                DiagnosticStep.Succeeded("Workspace query", TimeSpan.FromMilliseconds(2))));
        }
    }
}
