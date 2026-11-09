using System.Diagnostics;
using Azure;
using Azure.Identity;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;

namespace MonitoringApp;

public sealed class LogAnalyticsConnectionDiagnostics(
    CrossTenantLogAnalyticsOptions options,
    ILogAnalyticsSecretProvider secretProvider,
    ILogAnalyticsWorkspaceProbe workspaceProbe,
    ILogger<LogAnalyticsConnectionDiagnostics> logger)
{
    public LogAnalyticsConnectionDiagnostics(CrossTenantLogAnalyticsOptions options,
        ILogAnalyticsSecretProvider secretProvider, ILogAnalyticsWorkspaceProbe workspaceProbe)
        : this(options, secretProvider, workspaceProbe, NullLogger<LogAnalyticsConnectionDiagnostics>.Instance) { }

    public async Task<LogAnalyticsDiagnosticsReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        if (!options.Enabled)
        {
            return new LogAnalyticsDiagnosticsReport(
                checkedAt,
                DiagnosticStep.Disabled("Key Vault", "Cross-tenant Log Analytics is disabled.") with
                { NameCode = DiagnosticNameCode.KeyVault, MessageCode = DiagnosticMessageCode.Disabled },
                []);
        }

        var secretWatch = Stopwatch.StartNew();
        string secret;
        try
        {
            secret = await secretProvider.GetClientSecretAsync(cancellationToken);
        }
        catch (AuthenticationFailedException exception)
        {
            logger.LogWarning(exception, "Key Vault diagnostic authentication failed.");
            return FailedSecretReport(
                checkedAt,
                secretWatch.Elapsed,
                "Managed identity authentication to Key Vault failed.", DiagnosticMessageCode.KeyVaultAuthenticationFailed);
        }
        catch (RequestFailedException exception)
        {
            logger.LogWarning(exception, "Key Vault diagnostic request failed.");
            return FailedSecretReport(
                checkedAt,
                secretWatch.Elapsed,
                $"Key Vault request failed with HTTP {exception.Status}" +
                $"{(string.IsNullOrWhiteSpace(exception.ErrorCode) ? "." : $" ({exception.ErrorCode}).")}",
                DiagnosticMessageCode.KeyVaultRequestFailed, exception.Status);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Key Vault diagnostic request timed out.");
            return FailedSecretReport(checkedAt, secretWatch.Elapsed, "Key Vault request timed out.", DiagnosticMessageCode.KeyVaultTimeout);
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(exception, "Key Vault diagnostic secret retrieval failed.");
            return FailedSecretReport(checkedAt, secretWatch.Elapsed, exception.Message, DiagnosticMessageCode.KeyVaultFailed);
        }

        var secretStep = DiagnosticStep.Succeeded("Key Vault", secretWatch.Elapsed) with
        { NameCode = DiagnosticNameCode.KeyVault, MessageCode = DiagnosticMessageCode.Succeeded };
        var timeout = TimeSpan.FromSeconds(options.DiagnosticTimeoutSeconds);
        var workspaceResults = new List<LogAnalyticsWorkspaceDiagnostics>(options.Workspaces.Length);
        foreach (var workspace in options.Workspaces)
        {
            var result = await workspaceProbe.ProbeAsync(
                workspace,
                options.ApplicationClientId,
                secret,
                timeout,
                cancellationToken);
            workspaceResults.Add(new LogAnalyticsWorkspaceDiagnostics(
                workspace.Key,
                workspace.DisplayName,
                result.Token,
                result.Query));
        }

        return new LogAnalyticsDiagnosticsReport(checkedAt, secretStep, workspaceResults);
    }

    private LogAnalyticsDiagnosticsReport FailedSecretReport(
        DateTimeOffset checkedAt,
        TimeSpan elapsed,
        string message, DiagnosticMessageCode code, int? httpStatus = null) =>
        new(
            checkedAt,
            DiagnosticStep.Failed("Key Vault", elapsed, message) with
            { NameCode = DiagnosticNameCode.KeyVault, MessageCode = code, HttpStatus = httpStatus },
            options.Workspaces.Select(workspace => new LogAnalyticsWorkspaceDiagnostics(
                workspace.Key,
                workspace.DisplayName,
                DiagnosticStep.NotRun("Tenant token", "Key Vault access did not succeed.") with
                { NameCode = DiagnosticNameCode.TenantToken, MessageCode = DiagnosticMessageCode.KeyVaultRequired },
                DiagnosticStep.NotRun("Workspace query", "Key Vault access did not succeed.") with
                { NameCode = DiagnosticNameCode.WorkspaceQuery, MessageCode = DiagnosticMessageCode.KeyVaultRequired })).ToArray());
}

public sealed record LogAnalyticsDiagnosticsReport(
    DateTimeOffset CheckedAt,
    DiagnosticStep KeyVault,
    IReadOnlyList<LogAnalyticsWorkspaceDiagnostics> Workspaces);

public sealed record LogAnalyticsWorkspaceDiagnostics(
    string Key,
    string DisplayName,
    DiagnosticStep Token,
    DiagnosticStep Query);

public sealed record LogAnalyticsWorkspaceProbeResult(
    DiagnosticStep Token,
    DiagnosticStep Query);

public sealed record DiagnosticStep(
    string Name,
    DiagnosticStatus Status,
    long DurationMilliseconds,
    string Message,
    string? CorrelationId = null)
{
    [JsonIgnore]
    public DiagnosticNameCode? NameCode { get; init; }
    [JsonIgnore]
    public DiagnosticMessageCode? MessageCode { get; init; }
    [JsonIgnore]
    public int? HttpStatus { get; init; }

    public static DiagnosticStep Succeeded(
        string name,
        TimeSpan elapsed,
        string? correlationId = null) =>
        new(name, DiagnosticStatus.Succeeded, (long)elapsed.TotalMilliseconds, "Succeeded.", correlationId);

    public static DiagnosticStep Failed(
        string name,
        TimeSpan elapsed,
        string message,
        string? correlationId = null) =>
        new(name, DiagnosticStatus.Failed, (long)elapsed.TotalMilliseconds, message, correlationId);

    public static DiagnosticStep NotRun(string name, string message) =>
        new(name, DiagnosticStatus.NotRun, 0, message);

    public static DiagnosticStep Disabled(string name, string message) =>
        new(name, DiagnosticStatus.Disabled, 0, message);
}

public enum DiagnosticNameCode { KeyVault, TenantToken, WorkspaceQuery }

public enum DiagnosticMessageCode
{
    Succeeded,
    Disabled,
    KeyVaultAuthenticationFailed,
    KeyVaultRequestFailed,
    KeyVaultTimeout,
    KeyVaultFailed,
    KeyVaultRequired,
    TokenAuthenticationFailed,
    TokenTimeout,
    TokenRequired,
    QueryUnexpected,
    QueryRequestFailed,
    QueryTimeout
}

public enum DiagnosticStatus
{
    Succeeded,
    Failed,
    NotRun,
    Disabled
}
