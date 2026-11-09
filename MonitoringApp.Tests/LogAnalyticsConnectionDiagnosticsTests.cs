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
        Assert.Equal(DiagnosticNameCode.KeyVault, report.KeyVault.NameCode);
        Assert.Equal(DiagnosticMessageCode.Disabled, report.KeyVault.MessageCode);
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
        Assert.Equal(DiagnosticMessageCode.KeyVaultFailed, report.KeyVault.MessageCode);
        Assert.Equal("The configured secret is empty.", report.KeyVault.Message);
        Assert.All(report.Workspaces, workspace =>
        {
            Assert.Equal(DiagnosticStatus.NotRun, workspace.Token.Status);
            Assert.Equal(DiagnosticStatus.NotRun, workspace.Query.Status);
            Assert.Equal(DiagnosticMessageCode.KeyVaultRequired, workspace.Token.MessageCode);
            Assert.Equal(DiagnosticMessageCode.KeyVaultRequired, workspace.Query.MessageCode);
            Assert.Equal(DiagnosticNameCode.TenantToken, workspace.Token.NameCode);
            Assert.Equal(DiagnosticNameCode.WorkspaceQuery, workspace.Query.NameCode);
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
        Assert.Equal(DiagnosticMessageCode.Succeeded, report.KeyVault.MessageCode);
        Assert.Equal(2, report.Workspaces.Count);
        Assert.All(report.Workspaces, workspace =>
        {
            Assert.Equal(DiagnosticStatus.Succeeded, workspace.Token.Status);
            Assert.Equal(DiagnosticStatus.Succeeded, workspace.Query.Status);
        });
        Assert.Equal(1, secret.CallCount);
        Assert.Equal(2, probe.CallCount);
    }

    [Theory]
    [InlineData("authentication", "Managed identity authentication to Key Vault failed.")]
    [InlineData("request", "Key Vault request failed with HTTP 403 (Forbidden).")]
    [InlineData("timeout", "Key Vault request timed out.")]
    [InlineData("secret", "The configured secret is empty.")]
    public async Task SecretFailuresKeepTheirDiagnosticCategoryWithoutAdapterDetails(string failure, string message)
    {
        var options = CrossTenantLogAnalyticsOptionsTests.CreateValid();
        var secret = new FakeSecretProvider
        {
            Exception = failure switch
            {
                "authentication" => new Azure.Identity.AuthenticationFailedException("adapter diagnostic detail"),
                "request" => new Azure.RequestFailedException(403, "adapter diagnostic detail", "Forbidden", null),
                "timeout" => new TaskCanceledException("adapter diagnostic detail"),
                "secret" => new InvalidOperationException("The configured secret is empty."),
                _ => throw new ArgumentOutOfRangeException(nameof(failure))
            }
        };
        var probe = new FakeWorkspaceProbe();
        var logger = new DiagnosticLogger();

        var report = await new LogAnalyticsConnectionDiagnostics(options, secret, probe, logger).RunAsync();

        Assert.Equal(DiagnosticStatus.Failed, report.KeyVault.Status);
        Assert.Equal(message, report.KeyVault.Message);
        Assert.Equal(failure switch
        {
            "authentication" => DiagnosticMessageCode.KeyVaultAuthenticationFailed,
            "request" => DiagnosticMessageCode.KeyVaultRequestFailed,
            "timeout" => DiagnosticMessageCode.KeyVaultTimeout,
            _ => DiagnosticMessageCode.KeyVaultFailed
        }, report.KeyVault.MessageCode);
        Assert.Equal(failure == "request" ? 403 : (int?)null, report.KeyVault.HttpStatus);
        var workspace = Assert.Single(report.Workspaces);
        Assert.Equal(DiagnosticStatus.NotRun, workspace.Token.Status);
        Assert.Equal(DiagnosticStatus.NotRun, workspace.Query.Status);
        Assert.Equal(1, secret.CallCount);
        Assert.Equal(0, probe.CallCount);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, entry.Level);
        Assert.Same(secret.Exception, entry.Error);
    }

    [Fact]
    public async Task MixedProbeOutcomesPreserveRequestContextWorkspaceOrderAndIndependentSteps()
    {
        var options = CrossTenantLogAnalyticsOptionsTests.CreateValid(
        [
            CrossTenantLogAnalyticsOptionsTests.Workspace(
                "second", "22222222-2222-2222-2222-222222222222", "second.example"),
            CrossTenantLogAnalyticsOptionsTests.Workspace(
                "first", "11111111-1111-1111-1111-111111111111", "first.example"),
            CrossTenantLogAnalyticsOptionsTests.Workspace(
                "third", "33333333-3333-3333-3333-333333333333", "third.example")
        ], diagnosticTimeoutSeconds: 37);
        var secret = new FakeSecretProvider();
        var probe = new FakeWorkspaceProbe();
        probe.Results["second"] = new(
            DiagnosticStep.Failed("Tenant token", TimeSpan.FromMilliseconds(7), "Token rejected.", "token-request"),
            DiagnosticStep.NotRun("Workspace query", "Token acquisition did not succeed."));
        probe.Results["first"] = new(
            DiagnosticStep.Succeeded("Tenant token", TimeSpan.FromMilliseconds(11)),
            DiagnosticStep.Failed("Workspace query", TimeSpan.FromMilliseconds(19), "HTTP 403.", "query-request"));
        probe.Results["third"] = new(
            DiagnosticStep.Succeeded("Tenant token", TimeSpan.FromMilliseconds(23)),
            DiagnosticStep.Succeeded("Workspace query", TimeSpan.FromMilliseconds(29), "success-request"));
        using var cancellation = new CancellationTokenSource();

        var report = await new LogAnalyticsConnectionDiagnostics(options, secret, probe).RunAsync(cancellation.Token);

        Assert.Equal(DiagnosticStatus.Succeeded, report.KeyVault.Status);
        Assert.Equal(1, secret.CallCount);
        Assert.Equal(cancellation.Token, secret.Token);
        Assert.Equal(3, probe.CallCount);
        Assert.Equal(["second", "first", "third"], report.Workspaces.Select(workspace => workspace.Key));
        Assert.Equal(options.Workspaces.Length, probe.Requests.Count);
        for (var index = 0; index < options.Workspaces.Length; index++)
        {
            var configured = options.Workspaces[index];
            var request = probe.Requests[index];
            var result = report.Workspaces[index];
            Assert.Same(configured, request.Workspace);
            Assert.Equal(options.ApplicationClientId, request.ApplicationClientId);
            Assert.Equal("test-secret", request.Secret);
            Assert.Equal(TimeSpan.FromSeconds(37), request.Timeout);
            Assert.Equal(cancellation.Token, request.Token);
            Assert.Equal(configured.DisplayName, result.DisplayName);
            Assert.Equal(probe.Results[configured.Key].Token, result.Token);
            Assert.Equal(probe.Results[configured.Key].Query, result.Query);
        }
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedToASecretTimeoutReport()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var secret = new FakeSecretProvider { Exception = new OperationCanceledException(cancellation.Token) };
        var probe = new FakeWorkspaceProbe();
        var diagnostics = new LogAnalyticsConnectionDiagnostics(
            CrossTenantLogAnalyticsOptionsTests.CreateValid(), secret, probe);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => diagnostics.RunAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(cancellation.Token, secret.Token);
        Assert.Equal(1, secret.CallCount);
        Assert.Equal(0, probe.CallCount);
    }

    private sealed class DiagnosticLogger : Microsoft.Extensions.Logging.ILogger<LogAnalyticsConnectionDiagnostics>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, Exception? Error)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Entries.Add((level, exception));
    }

    private sealed class FakeSecretProvider : ILogAnalyticsSecretProvider
    {
        public int CallCount { get; private set; }
        public Exception? Exception { get; init; }
        public CancellationToken Token { get; private set; }

        public Task<string> GetClientSecretAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            Token = cancellationToken;
            return Exception is null
                ? Task.FromResult("test-secret")
                : Task.FromException<string>(Exception);
        }
    }

    private sealed class FakeWorkspaceProbe : ILogAnalyticsWorkspaceProbe
    {
        public int CallCount { get; private set; }
        public Dictionary<string, LogAnalyticsWorkspaceProbeResult> Results { get; } = [];
        public List<(CrossTenantLogAnalyticsWorkspaceOptions Workspace, string ApplicationClientId,
            string Secret, TimeSpan Timeout, CancellationToken Token)> Requests { get; } = [];

        public Task<LogAnalyticsWorkspaceProbeResult> ProbeAsync(
            CrossTenantLogAnalyticsWorkspaceOptions workspace,
            string applicationClientId,
            string clientSecret,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Requests.Add((workspace, applicationClientId, clientSecret, timeout, cancellationToken));
            if (Results.TryGetValue(workspace.Key, out var result))
                return Task.FromResult(result);
            return Task.FromResult(new LogAnalyticsWorkspaceProbeResult(
                DiagnosticStep.Succeeded("Tenant token", TimeSpan.FromMilliseconds(1)),
                DiagnosticStep.Succeeded("Workspace query", TimeSpan.FromMilliseconds(2))));
        }
    }
}
