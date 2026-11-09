using System.Collections.Concurrent;
using System.Net;

namespace MonitoringApp;

public sealed record MicrosoftSource(
    string Id,
    string Title,
    Uri Url,
    string Product,
    IReadOnlyList<string> Topics,
    IReadOnlyList<string> SymptomsOrErrorCodes,
    string ValidationStatus,
    DateOnly LastCheckedOn);

public sealed class MicrosoftSourceCatalog
{
    public const string CurrentVersion = "microsoft-sources-2026-09-16.1";
    public const string CuratedOnlyMode = "CuratedOnly";

    private static readonly string[] DefaultAllowedHosts =
    [
        "learn.microsoft.com",
        "support.microsoft.com"
    ];

    private static readonly string[] DefaultAllowedProducts =
    [
        "Windows Server Active Directory Domain Services",
        "Azure Monitor",
        "Azure Log Analytics",
        "Azure Arc",
        "Microsoft Entra",
        "Azure Logic Apps"
    ];
    private readonly ConcurrentDictionary<string, string> blockedSources =
        new(StringComparer.OrdinalIgnoreCase);

    public MicrosoftSourceCatalog(
        string version,
        IReadOnlyList<MicrosoftSource> sources,
        IReadOnlyCollection<string>? allowedHosts = null,
        IReadOnlyCollection<string>? allowedProducts = null,
        bool runtimeSearchEnabled = false)
    {
        Version = version;
        Sources = sources;
        AllowedHosts = new HashSet<string>(
            allowedHosts ?? DefaultAllowedHosts,
            StringComparer.OrdinalIgnoreCase);
        AllowedProducts = new HashSet<string>(
            allowedProducts ?? DefaultAllowedProducts,
            StringComparer.OrdinalIgnoreCase);
        RuntimeSearchEnabled = runtimeSearchEnabled;

        var errors = Validate();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Invalid Microsoft source catalog: {string.Join(" ", errors)}");
        }
    }

    public string Version { get; }
    public string SearchMode => RuntimeSearchEnabled ? "ControlledMicrosoftSearch" : CuratedOnlyMode;
    public bool RuntimeSearchEnabled { get; }
    public IReadOnlyList<MicrosoftSource> Sources { get; }
    public IReadOnlyList<MicrosoftSource> ApprovedSources =>
        Sources.Where(source => !blockedSources.ContainsKey(source.Id)).ToArray();
    public IReadOnlySet<string> AllowedHosts { get; }
    public IReadOnlySet<string> AllowedProducts { get; }

    public static MicrosoftSourceCatalog CreateDefault() => new(
        CurrentVersion,
        [
            Source(
                "ms-ad-ds-troubleshooting",
                "AD DS troubleshooting",
                "https://learn.microsoft.com/windows-server/identity/ad-ds/manage/ad-ds-troubleshooting",
                ["AD DS", "diagnostics", "DCDiag"],
                ["General AD DS health"]),
            Source(
                "ms-ad-replication-troubleshooting",
                "Troubleshooting Active Directory replication problems",
                "https://learn.microsoft.com/windows-server/identity/ad-ds/manage/troubleshoot/troubleshooting-active-directory-replication-problems",
                ["AD replication", "topology", "DNS", "RPC"],
                ["Replication failure"]),
            Source(
                "ms-ad-repadmin-diagnostics",
                "Diagnose Active Directory replication failures",
                "https://learn.microsoft.com/troubleshoot/windows-server/active-directory/diagnose-replication-failures",
                ["AD replication", "Repadmin", "read-only check"],
                ["Replication failure", "repadmin /showrepl"]),
            Source(
                "ms-ad-rpc-1722",
                "Active Directory replication error 1722: The RPC server is unavailable",
                "https://learn.microsoft.com/troubleshoot/windows-server/active-directory/replication-error-1722-rpc-server-unavailable",
                ["AD replication", "RPC", "DNS", "connectivity"],
                ["1722", "0x6ba", "RPC_S_SERVER_UNAVAILABLE"]),
            Source(
                "ms-ad-dns-event-2087",
                "Active Directory replication Event ID 2087: DNS lookup failure caused replication to fail",
                "https://learn.microsoft.com/troubleshoot/windows-server/active-directory/active-directory-replication-event-id-2087",
                ["AD replication", "DNS", "DCDiag"],
                ["Event ID 2087", "DNS lookup failure"]),
            Source(
                "ms-ad-firewall-ports",
                "How to configure a firewall for Active Directory domains and trusts",
                "https://learn.microsoft.com/troubleshoot/windows-server/active-directory/config-firewall-for-ad-domains-and-trusts",
                ["AD DS", "firewall", "ports", "RPC"],
                ["Blocked port", "RPC connectivity"]),
            Source(
                "ms-ad-portqry",
                "Use PortQry to verify Active Directory TCP/IP connectivity",
                "https://learn.microsoft.com/troubleshoot/windows-server/networking/use-portqry-verify-active-directory-tcp-ip-connectivity",
                ["AD DS", "PortQry", "connectivity", "read-only check"],
                ["Blocked port", "TCP/IP connectivity"])
        ]);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Version))
        {
            errors.Add("Version is required.");
        }

        if (RuntimeSearchEnabled)
        {
            errors.Add("Runtime Microsoft search is not approved. Search queries, results, retrieval time, and excerpts require an audited implementation first.");
        }

        foreach (var duplicate in Sources.GroupBy(source => source.Id, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            errors.Add($"Source ID '{duplicate.Key}' is duplicated.");
        }

        foreach (var duplicate in Sources.GroupBy(source => source.Url.AbsoluteUri, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            errors.Add($"Source URL '{duplicate.Key}' is duplicated.");
        }

        foreach (var source in Sources)
        {
            ValidateSource(source, errors);
        }

        return errors;
    }

    public bool IsAllowedUri(Uri uri) =>
        uri.IsAbsoluteUri &&
        uri.Scheme == Uri.UriSchemeHttps &&
        AllowedHosts.Contains(uri.IdnHost);

    public bool IsBlocked(string sourceId) => blockedSources.ContainsKey(sourceId);

    public string? GetBlockReason(string sourceId) =>
        blockedSources.TryGetValue(sourceId, out var reason) ? reason : null;

    internal void MarkReachable(string sourceId) => blockedSources.TryRemove(sourceId, out _);

    internal void Block(string sourceId, string reason) => blockedSources[sourceId] = reason;

    private void ValidateSource(MicrosoftSource source, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(source.Id) ||
            string.IsNullOrWhiteSpace(source.Title) ||
            string.IsNullOrWhiteSpace(source.ValidationStatus))
        {
            errors.Add("Every source requires ID, title, and validation status.");
        }

        if (!IsAllowedUri(source.Url))
        {
            errors.Add($"Source '{source.Id}' must use HTTPS on an approved Microsoft host.");
        }

        if (!AllowedProducts.Contains(source.Product))
        {
            errors.Add($"Source '{source.Id}' uses unapproved product '{source.Product}'.");
        }

        if (source.Topics.Count == 0 || source.SymptomsOrErrorCodes.Count == 0)
        {
            errors.Add($"Source '{source.Id}' requires topics and symptoms or error codes.");
        }

        if (source.LastCheckedOn == default || source.LastCheckedOn > DateOnly.FromDateTime(DateTime.UtcNow))
        {
            errors.Add($"Source '{source.Id}' has an invalid last-checked date.");
        }
    }

    private static MicrosoftSource Source(
        string id,
        string title,
        string url,
        IReadOnlyList<string> topics,
        IReadOnlyList<string> symptomsOrErrorCodes) =>
        new(
            id,
            title,
            new Uri(url, UriKind.Absolute),
            "Windows Server Active Directory Domain Services",
            topics,
            symptomsOrErrorCodes,
            "Verified against Microsoft Learn",
            new DateOnly(2026, 9, 16));
}

public sealed record MicrosoftSourceLinkCheckResult(
    string SourceId,
    bool Reachable,
    Uri? FinalUrl,
    string? FailureReason);

public sealed class MicrosoftSourceLinkChecker(
    HttpClient httpClient,
    MicrosoftSourceCatalog catalog)
{
    private const int MaxRedirects = 5;

    public async Task<IReadOnlyList<MicrosoftSourceLinkCheckResult>> VerifyAllAsync(
        CancellationToken cancellationToken = default)
    {
        var checks = catalog.Sources.Select(source => VerifyAsync(source, cancellationToken));
        return await Task.WhenAll(checks);
    }

    public async Task<MicrosoftSourceLinkCheckResult> VerifyAsync(
        MicrosoftSource source,
        CancellationToken cancellationToken = default)
    {
        var currentUrl = source.Url;

        try
        {
            for (var redirectCount = 0; redirectCount <= MaxRedirects; redirectCount++)
            {
                if (!catalog.IsAllowedUri(currentUrl))
                {
                    return Block(source, $"URL or redirect target is not an approved HTTPS Microsoft host: {currentUrl}");
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, currentUrl);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (IsRedirect(response.StatusCode))
                {
                    if (response.Headers.Location is null)
                    {
                        return Block(source, $"Redirect from {currentUrl} has no Location header.");
                    }

                    currentUrl = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(currentUrl, response.Headers.Location);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return Block(source, $"Microsoft source returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");
                }

                catalog.MarkReachable(source.Id);
                return new MicrosoftSourceLinkCheckResult(source.Id, true, currentUrl, null);
            }

            return Block(source, $"Microsoft source exceeded the limit of {MaxRedirects} redirects.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return Block(source, $"Microsoft source check failed: {exception.Message}");
        }
    }

    private MicrosoftSourceLinkCheckResult Block(MicrosoftSource source, string reason)
    {
        catalog.Block(source.Id, reason);
        return new MicrosoftSourceLinkCheckResult(source.Id, false, null, reason);
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;
}

public sealed class MicrosoftSourceCatalogVerificationService(
    MicrosoftSourceLinkChecker checker,
    ILogger<MicrosoftSourceCatalogVerificationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await VerifyAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await VerifyAsync(stoppingToken);
        }
    }

    private async Task VerifyAsync(CancellationToken cancellationToken)
    {
        var results = await checker.VerifyAllAsync(cancellationToken);
        foreach (var result in results)
        {
            if (result.Reachable)
            {
                logger.LogInformation(
                    "Microsoft source {SourceId} passed reachability validation. FinalUrl={FinalUrl}",
                    result.SourceId,
                    result.FinalUrl);
            }
            else
            {
                logger.LogWarning(
                    "Microsoft source {SourceId} is blocked: {Reason}",
                    result.SourceId,
                    result.FailureReason);
            }
        }
    }
}