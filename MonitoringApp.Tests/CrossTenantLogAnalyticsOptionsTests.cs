using System.Text.Json;
using Microsoft.Extensions.Configuration;

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

    [Fact]
    public void BindsDomainDisplayNamesWithoutDuplicatingWorkspaceRoutes()
    {
        var configured = CreateValid();
        var original = configured.Workspaces[0];
        configured.Workspaces[0] = new()
        {
            Key = original.Key, DisplayName = original.DisplayName,
            TenantId = original.TenantId, WorkspaceId = original.WorkspaceId,
            SubscriptionIds = original.SubscriptionIds,
            Domains = ["alpha.example", "beta.example"],
            DomainDisplayNames = new() { ["ALPHA.EXAMPLE."] = "Company A", ["beta.example"] = "Company B" }
        };
        using var json = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { CrossTenantLogAnalytics = configured }));
        var configuration = new ConfigurationBuilder().AddJsonStream(json).Build();
        var bound = configuration.GetSection(CrossTenantLogAnalyticsOptions.SectionName).Get<CrossTenantLogAnalyticsOptions>()!;

        Assert.Empty(bound.Validate());
        var workspace = Assert.Single(bound.Workspaces);
        Assert.Equal("Company A", workspace.GetDomainDisplayName(" alpha.example "));
        Assert.Equal("Company B", workspace.GetDomainDisplayName("BETA.EXAMPLE."));
        var resolver = new TargetWorkspaceResolver(bound);
        Assert.Same(workspace, resolver.Resolve(original.SubscriptionIds[0], "alpha.example").Workspace);
        Assert.Same(workspace, resolver.Resolve(original.SubscriptionIds[0], "beta.example").Workspace);
    }

    [Fact]
    public void LegacyDomainsKeepTheirNamesWithoutOptionalDisplayNames()
    {
        var options = CreateValid();
        var workspace = Assert.Single(options.Workspaces);

        Assert.Empty(options.Validate());
        Assert.Empty(workspace.DomainDisplayNames);
        Assert.Equal("corp.example", workspace.GetDomainDisplayName("corp.example"));
    }

    [Theory]
    [InlineData("", "Company A", "must match a domain")]
    [InlineData("unknown.example", "Company A", "must match a domain")]
    [InlineData("corp.example", "", "must not be empty")]
    [InlineData("corp.example", "   ", "must not be empty")]
    public void RejectsInvalidDomainDisplayNames(string domain, string name, string message)
    {
        var options = CreateValid();
        var original = options.Workspaces[0];
        options.Workspaces[0] = new()
        {
            Key = original.Key, DisplayName = original.DisplayName,
            TenantId = original.TenantId, WorkspaceId = original.WorkspaceId,
            SubscriptionIds = original.SubscriptionIds, Domains = original.Domains,
            DomainDisplayNames = new() { [domain] = name }
        };

        Assert.Contains(options.Validate(), error => error.Contains("DomainDisplayNames", StringComparison.Ordinal) &&
            error.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsAmbiguousNormalizedDomainDisplayNameKeys()
    {
        var options = CreateValid();
        var original = options.Workspaces[0];
        options.Workspaces[0] = new()
        {
            Key = original.Key, DisplayName = original.DisplayName,
            TenantId = original.TenantId, WorkspaceId = original.WorkspaceId,
            SubscriptionIds = original.SubscriptionIds, Domains = original.Domains,
            DomainDisplayNames = new() { ["corp.example"] = "Company A", [" CORP.EXAMPLE. "] = "Company B" }
        };

        Assert.Contains(options.Validate(), error => error.Contains("domain keys must be unique", StringComparison.Ordinal));
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
