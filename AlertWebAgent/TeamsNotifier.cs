using System.Net.Http.Json;

namespace AlertWebAgent;

public sealed class TeamsNotifier(ILogger<TeamsNotifier> logger) : IDisposable
{
    private readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<bool> SendAsync(
        AlertObservation alert,
        AgentOptions options,
        CancellationToken cancellationToken)
    {
        if (options.DryRun)
        {
            logger.LogInformation(
                "DRY RUN: Would send {Condition} alert {AlertName} for {TargetName} to Teams.",
                alert.Condition,
                alert.Name,
                alert.TargetName);
            return true;
        }

        var isResolved = alert.Condition.Equals("Resolved", StringComparison.OrdinalIgnoreCase);
        var card = new
        {
            @type = "MessageCard",
            @context = "https://schema.org/extensions",
            summary = $"{alert.Condition}: {alert.Name}",
            themeColor = isResolved ? "2E7D32" : "C62828",
            title = $"{alert.Condition}: {alert.Name}",
            sections = new[]
            {
                new
                {
                    text = alert.Description,
                    facts = new[]
                    {
                        new { name = "Target", value = alert.TargetName },
                        new { name = "Resource group", value = alert.ResourceGroup },
                        new { name = "Subscription", value = alert.SubscriptionId },
                        new { name = "Received", value = alert.ReceivedAt },
                        new { name = "Comments", value = alert.Comments }
                    }
                }
            },
            potentialAction = CreateActions(alert, options)
        };

        using var response = await httpClient.PostAsJsonAsync(options.TeamsWebhookUrl, card, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "Teams rejected alert {AlertId} with HTTP {StatusCode}: {ResponseBody}",
                alert.Id,
                (int)response.StatusCode,
                responseBody);
            return false;
        }

        logger.LogInformation("Sent {Condition} alert {AlertName} to Teams.", alert.Condition, alert.Name);
        return true;
    }

    private static object[] CreateActions(AlertObservation alert, AgentOptions options)
    {
        var links = new List<object>();
        if (Uri.TryCreate(options.AlertConsoleUrl, UriKind.Absolute, out _))
        {
            links.Add(CreateOpenUriAction("Open Alert Console", options.AlertConsoleUrl));
        }

        if (Uri.TryCreate(alert.SearchResultsUrl, UriKind.Absolute, out _))
        {
            links.Add(CreateOpenUriAction("Open Search Results", alert.SearchResultsUrl));
        }

        return links.ToArray();
    }

    private static object CreateOpenUriAction(string name, string url) => new
    {
        @type = "OpenUri",
        name,
        targets = new[] { new { os = "default", uri = url } }
    };

    public void Dispose() => httpClient.Dispose();
}