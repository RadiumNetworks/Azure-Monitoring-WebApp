using System.Diagnostics;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Query.Logs;
using Azure.Security.KeyVault.Secrets;

namespace MonitoringApp;

public interface ILogAnalyticsSecretProvider
{
    Task<string> GetClientSecretAsync(CancellationToken cancellationToken);
}

public interface ILogAnalyticsWorkspaceProbe
{
    Task<LogAnalyticsWorkspaceProbeResult> ProbeAsync(
        CrossTenantLogAnalyticsWorkspaceOptions workspace,
        string applicationClientId,
        string clientSecret,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed class KeyVaultLogAnalyticsSecretProvider : ILogAnalyticsSecretProvider
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private readonly CrossTenantLogAnalyticsOptions options;
    private readonly SecretClient? client;
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private string? cachedSecret;
    private DateTimeOffset cachedUntil;

    public KeyVaultLogAnalyticsSecretProvider(CrossTenantLogAnalyticsOptions options)
    {
        this.options = options;
        if (!options.Enabled)
        {
            return;
        }

        var credential = new ManagedIdentityCredential(
            ManagedIdentityId.FromUserAssignedClientId(options.ManagedIdentityClientId));
        client = new SecretClient(new Uri(options.KeyVaultUri), credential);
    }

    public async Task<string> GetClientSecretAsync(CancellationToken cancellationToken)
    {
        if (client is null)
        {
            throw new InvalidOperationException("Cross-tenant Log Analytics is disabled.");
        }
        if (!string.IsNullOrEmpty(cachedSecret) && cachedUntil > DateTimeOffset.UtcNow)
        {
            return cachedSecret;
        }

        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrEmpty(cachedSecret) && cachedUntil > DateTimeOffset.UtcNow)
            {
                return cachedSecret;
            }

            var response = await client.GetSecretAsync(
                options.ClientSecretName,
                cancellationToken: cancellationToken);
            if (string.IsNullOrEmpty(response.Value.Value))
            {
                throw new InvalidOperationException("The configured Key Vault secret has an empty value.");
            }

            cachedSecret = response.Value.Value;
            cachedUntil = DateTimeOffset.UtcNow.Add(CacheDuration);
            return cachedSecret;
        }
        finally
        {
            refreshLock.Release();
        }
    }
}

public sealed class AzureLogAnalyticsWorkspaceProbe : ILogAnalyticsWorkspaceProbe
{
    private static readonly string[] Scopes = ["https://api.loganalytics.io/.default"];

    public async Task<LogAnalyticsWorkspaceProbeResult> ProbeAsync(
        CrossTenantLogAnalyticsWorkspaceOptions workspace,
        string applicationClientId,
        string clientSecret,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var tokenWatch = Stopwatch.StartNew();
        var credential = new ClientSecretCredential(
            workspace.TenantId,
            applicationClientId,
            clientSecret);

        try
        {
            await credential.GetTokenAsync(
                new TokenRequestContext(Scopes),
                timeoutSource.Token);
        }
        catch (AuthenticationFailedException)
        {
            return new LogAnalyticsWorkspaceProbeResult(
                DiagnosticStep.Failed("Tenant token", tokenWatch.Elapsed, "Microsoft Entra token acquisition failed."),
                DiagnosticStep.NotRun("Workspace query", "Token acquisition did not succeed."));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LogAnalyticsWorkspaceProbeResult(
                DiagnosticStep.Failed("Tenant token", tokenWatch.Elapsed, "Token acquisition timed out."),
                DiagnosticStep.NotRun("Workspace query", "Token acquisition did not succeed."));
        }

        var tokenStep = DiagnosticStep.Succeeded("Tenant token", tokenWatch.Elapsed);
        var queryWatch = Stopwatch.StartNew();
        try
        {
            var client = new LogsQueryClient(credential);
            var response = await client.QueryWorkspaceAsync<string>(
                workspace.WorkspaceId,
                "print Status = \"ok\"",
                new LogsQueryTimeRange(TimeSpan.FromMinutes(5)),
                cancellationToken: timeoutSource.Token);
            if (response.Value.Count != 1 ||
                !response.Value[0].Equals("ok", StringComparison.Ordinal))
            {
                return new LogAnalyticsWorkspaceProbeResult(
                    tokenStep,
                    DiagnosticStep.Failed(
                        "Workspace query",
                        queryWatch.Elapsed,
                        "The workspace returned an unexpected test-query result."));
            }

            var correlationId = response.GetRawResponse().Headers.TryGetValue(
                "x-ms-request-id",
                out var requestId)
                ? requestId
                : null;
            return new LogAnalyticsWorkspaceProbeResult(
                tokenStep,
                DiagnosticStep.Succeeded("Workspace query", queryWatch.Elapsed, correlationId));
        }
        catch (RequestFailedException exception)
        {
            return new LogAnalyticsWorkspaceProbeResult(
                tokenStep,
                DiagnosticStep.Failed(
                    "Workspace query",
                    queryWatch.Elapsed,
                    AzureFailureMessage(exception)));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LogAnalyticsWorkspaceProbeResult(
                tokenStep,
                DiagnosticStep.Failed("Workspace query", queryWatch.Elapsed, "Workspace query timed out."));
        }
    }

    private static string AzureFailureMessage(RequestFailedException exception) =>
        $"Azure request failed with HTTP {exception.Status}" +
        $"{(string.IsNullOrWhiteSpace(exception.ErrorCode) ? "." : $" ({exception.ErrorCode}).")}";
}

public sealed class LogAnalyticsConnectionDiagnostics(
    CrossTenantLogAnalyticsOptions options,
    ILogAnalyticsSecretProvider secretProvider,
    ILogAnalyticsWorkspaceProbe workspaceProbe)
{
    public async Task<LogAnalyticsDiagnosticsReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        if (!options.Enabled)
        {
            return new LogAnalyticsDiagnosticsReport(
                checkedAt,
                DiagnosticStep.Disabled("Key Vault", "Cross-tenant Log Analytics is disabled."),
                []);
        }

        var secretWatch = Stopwatch.StartNew();
        string secret;
        try
        {
            secret = await secretProvider.GetClientSecretAsync(cancellationToken);
        }
        catch (AuthenticationFailedException)
        {
            return FailedSecretReport(
                checkedAt,
                secretWatch.Elapsed,
                "Managed identity authentication to Key Vault failed.");
        }
        catch (RequestFailedException exception)
        {
            return FailedSecretReport(
                checkedAt,
                secretWatch.Elapsed,
                $"Key Vault request failed with HTTP {exception.Status}" +
                $"{(string.IsNullOrWhiteSpace(exception.ErrorCode) ? "." : $" ({exception.ErrorCode}).")}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FailedSecretReport(checkedAt, secretWatch.Elapsed, "Key Vault request timed out.");
        }
        catch (InvalidOperationException exception)
        {
            return FailedSecretReport(checkedAt, secretWatch.Elapsed, exception.Message);
        }

        var secretStep = DiagnosticStep.Succeeded("Key Vault", secretWatch.Elapsed);
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
        string message) =>
        new(
            checkedAt,
            DiagnosticStep.Failed("Key Vault", elapsed, message),
            options.Workspaces.Select(workspace => new LogAnalyticsWorkspaceDiagnostics(
                workspace.Key,
                workspace.DisplayName,
                DiagnosticStep.NotRun("Tenant token", "Key Vault access did not succeed."),
                DiagnosticStep.NotRun("Workspace query", "Key Vault access did not succeed."))).ToArray());
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

public enum DiagnosticStatus
{
    Succeeded,
    Failed,
    NotRun,
    Disabled
}
