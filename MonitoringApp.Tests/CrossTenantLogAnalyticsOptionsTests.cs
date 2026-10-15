namespace MonitoringApp.Tests;

public sealed class CrossTenantLogAnalyticsOptionsTests
{
    [Fact]
    public void DisabledConfigurationDoesNotRequireAzureValues()
    {
        Assert.Empty(new CrossTenantLogAnalyticsOptions().Validate());
    }

    [Fact]
    public void AcceptsValidMultiTenantConfiguration()
    {
        Assert.Empty(CreateValid().Validate());
    }

    [Fact]
    public void RejectsDuplicateRoutesAcrossWorkspaces()
    {
        var first = Workspace(
            "first",
            "11111111-1111-1111-1111-111111111111",
            "corp.example");
        var second = Workspace(
            "second",
            "11111111-1111-1111-1111-111111111111",
            "CORP.EXAMPLE.");
        var options = CreateValid([first, second]);

        var errors = options.Validate();

        Assert.Contains(errors, error => error.Contains("subscription IDs must be unique", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("domains must be unique", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsInvalidCredentialAndWorkspaceValues()
    {
        var options = new CrossTenantLogAnalyticsOptions
        {
            Enabled = true,
            ManagedIdentityClientId = "not-a-guid",
            ApplicationClientId = "also-not-a-guid",
            KeyVaultUri = "http://vault.example",
            ClientSecretName = "invalid secret",
            DiagnosticTimeoutSeconds = 1,
            Workspaces = [new CrossTenantLogAnalyticsWorkspaceOptions()]
        };

        var errors = options.Validate();

        Assert.Contains(errors, error => error.Contains("ManagedIdentityClientId", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("ApplicationClientId", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("KeyVaultUri", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("ClientSecretName", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("DiagnosticTimeoutSeconds", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Workspaces:0", StringComparison.Ordinal));
    }

    internal static CrossTenantLogAnalyticsOptions CreateValid(
        CrossTenantLogAnalyticsWorkspaceOptions[]? workspaces = null) =>
        new()
        {
            Enabled = true,
            ManagedIdentityClientId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            ApplicationClientId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            KeyVaultUri = "https://monitoring.vault.azure.net/",
            ClientSecretName = "log-analytics-client-secret",
            Workspaces = workspaces ??
            [
                Workspace(
                    "primary",
                    "11111111-1111-1111-1111-111111111111",
                    "corp.example")
            ]
        };

    internal static CrossTenantLogAnalyticsWorkspaceOptions Workspace(
        string key,
        string subscriptionId,
        string domain) =>
        new()
        {
            Key = key,
            DisplayName = $"{key} workspace",
            TenantId = Guid.NewGuid().ToString(),
            WorkspaceId = Guid.NewGuid().ToString(),
            SubscriptionIds = [subscriptionId],
            Domains = [domain]
        };
}
