using System.Diagnostics;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Query.Logs;
using Microsoft.Extensions.Logging.Abstractions;

namespace MonitoringApp;

public interface ILogAnalyticsWorkspaceProbe
{
    Task<LogAnalyticsWorkspaceProbeResult> ProbeAsync(
        CrossTenantLogAnalyticsWorkspaceOptions workspace,
        string applicationClientId,
        string clientSecret,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed class AzureLogAnalyticsWorkspaceProbe : ILogAnalyticsWorkspaceProbe
{
    private static readonly string[] Scopes = ["https://api.loganalytics.io/.default"];
    private readonly LogAnalyticsClientFactory clients;
    private readonly ILogger<AzureLogAnalyticsWorkspaceProbe> logger;

    public AzureLogAnalyticsWorkspaceProbe() : this(new LogAnalyticsClientFactory())
    {
    }

    public AzureLogAnalyticsWorkspaceProbe(ILogger<AzureLogAnalyticsWorkspaceProbe> logger)
        : this(new LogAnalyticsClientFactory(), logger) { }

    internal AzureLogAnalyticsWorkspaceProbe(LogAnalyticsClientFactory clients)
        : this(clients, NullLogger<AzureLogAnalyticsWorkspaceProbe>.Instance) { }

    internal AzureLogAnalyticsWorkspaceProbe(LogAnalyticsClientFactory clients, ILogger<AzureLogAnalyticsWorkspaceProbe> logger)
    {
        this.clients = clients;
        this.logger = logger;
    }

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
        var credential = clients.CreateCredential(workspace, applicationClientId, clientSecret);

        try
        {
            await credential.GetTokenAsync(
                new TokenRequestContext(Scopes),
                timeoutSource.Token);
        }
        catch (AuthenticationFailedException exception)
        {
            logger.LogWarning(exception, "Log Analytics diagnostic token authentication failed.");
            return new LogAnalyticsWorkspaceProbeResult(
                DiagnosticStep.Failed("Tenant token", tokenWatch.Elapsed, "Microsoft Entra token acquisition failed.") with
                { NameCode = DiagnosticNameCode.TenantToken, MessageCode = DiagnosticMessageCode.TokenAuthenticationFailed },
                DiagnosticStep.NotRun("Workspace query", "Token acquisition did not succeed.") with
                { NameCode = DiagnosticNameCode.WorkspaceQuery, MessageCode = DiagnosticMessageCode.TokenRequired });
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Log Analytics diagnostic token acquisition timed out.");
            return new LogAnalyticsWorkspaceProbeResult(
                DiagnosticStep.Failed("Tenant token", tokenWatch.Elapsed, "Token acquisition timed out.") with
                { NameCode = DiagnosticNameCode.TenantToken, MessageCode = DiagnosticMessageCode.TokenTimeout },
                DiagnosticStep.NotRun("Workspace query", "Token acquisition did not succeed.") with
                { NameCode = DiagnosticNameCode.WorkspaceQuery, MessageCode = DiagnosticMessageCode.TokenRequired });
        }

        var tokenStep = DiagnosticStep.Succeeded("Tenant token", tokenWatch.Elapsed) with
        { NameCode = DiagnosticNameCode.TenantToken, MessageCode = DiagnosticMessageCode.Succeeded };
        var queryWatch = Stopwatch.StartNew();
        try
        {
            var client = clients.CreateClient(credential);
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
                        "The workspace returned an unexpected test-query result.") with
                    { NameCode = DiagnosticNameCode.WorkspaceQuery, MessageCode = DiagnosticMessageCode.QueryUnexpected });
            }

            var correlationId = response.GetRawResponse().Headers.TryGetValue(
                "x-ms-request-id",
                out var requestId)
                ? requestId
                : null;
            return new LogAnalyticsWorkspaceProbeResult(
                tokenStep,
                DiagnosticStep.Succeeded("Workspace query", queryWatch.Elapsed, correlationId) with
                { NameCode = DiagnosticNameCode.WorkspaceQuery, MessageCode = DiagnosticMessageCode.Succeeded });
        }
        catch (RequestFailedException exception)
        {
            logger.LogWarning(exception, "Log Analytics diagnostic query request failed.");
            return new LogAnalyticsWorkspaceProbeResult(
                tokenStep,
                DiagnosticStep.Failed(
                    "Workspace query",
                    queryWatch.Elapsed,
                    AzureFailureMessage(exception)) with
                { NameCode = DiagnosticNameCode.WorkspaceQuery, MessageCode = DiagnosticMessageCode.QueryRequestFailed,
                    HttpStatus = exception.Status });
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Log Analytics diagnostic query timed out.");
            return new LogAnalyticsWorkspaceProbeResult(
                tokenStep,
                DiagnosticStep.Failed("Workspace query", queryWatch.Elapsed, "Workspace query timed out.") with
                { NameCode = DiagnosticNameCode.WorkspaceQuery, MessageCode = DiagnosticMessageCode.QueryTimeout });
        }
    }

    private static string AzureFailureMessage(RequestFailedException exception) =>
        $"Azure request failed with HTTP {exception.Status}" +
        $"{(string.IsNullOrWhiteSpace(exception.ErrorCode) ? "." : $" ({exception.ErrorCode}).")}";
}
