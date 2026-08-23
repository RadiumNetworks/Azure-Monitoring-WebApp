namespace AlertWebAgent;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public string PageUrl { get; set; } = string.Empty;
    public int PollIntervalSeconds { get; set; } = 60;
    public int NavigationTimeoutSeconds { get; set; } = 30;
    public string StateFilePath { get; set; } = "data/seen-alerts.json";
    public string BrowserStorageStatePath { get; set; } = string.Empty;
    public string TeamsWebhookUrl { get; set; } = string.Empty;
    public string AlertConsoleUrl { get; set; } = string.Empty;
    public bool NotifyExistingOnFirstRun { get; set; }
    public bool Headless { get; set; } = true;
    public bool DryRun { get; set; } = true;
    public bool RunOnce { get; set; }

    public void Validate()
    {
        if (!Uri.TryCreate(PageUrl, UriKind.Absolute, out var pageUri) ||
            (pageUri.Scheme != Uri.UriSchemeHttp && pageUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Agent:PageUrl must be an absolute HTTP or HTTPS URL.");
        }

        if (PollIntervalSeconds < 5)
        {
            throw new InvalidOperationException("Agent:PollIntervalSeconds must be at least 5 seconds.");
        }

        if (NavigationTimeoutSeconds < 5)
        {
            throw new InvalidOperationException("Agent:NavigationTimeoutSeconds must be at least 5 seconds.");
        }

        if (!DryRun && !Uri.TryCreate(TeamsWebhookUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("Agent:TeamsWebhookUrl is required when DryRun is false.");
        }
    }
}