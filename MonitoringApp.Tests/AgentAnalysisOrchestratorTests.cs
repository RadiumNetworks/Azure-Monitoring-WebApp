namespace MonitoringApp.Tests;

public sealed class AgentAnalysisOrchestratorTests
{
    [Fact]
    public void CopilotPromptProducesVersionedPreparationWithoutTransportProfile()
    {
        var state = AgentAnalysisExecutionState.Create(new AgentAnalysisExecutionOptions());
        var orchestrator = new AgentAnalysisOrchestrator(state, MicrosoftSourceCatalog.CreateDefault());

        var preparation = orchestrator.Prepare(CreateSnapshot());

        Assert.True(state.IsEnabled);
        Assert.Equal(AgentAnalysisExecutionModes.CopilotPrompt, preparation.Mode);
        Assert.Null(preparation.Profile);
        Assert.Equal(AlertAgentPrompt.SnapshotPromptVersion, preparation.PromptVersion);
        Assert.Equal(AgentAnalysisResultContract.CurrentVersion, preparation.ResultContractVersion);
        Assert.Contains("BEGIN_UNTRUSTED_INCIDENT_SNAPSHOT_JSON", preparation.Prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Unknown", null, null)]
    [InlineData("TeamsWorkflow", null, null)]
    [InlineData("TeamsWorkflow", "teams-approved-name", null)]
    [InlineData("Api", null, "api-approved-name")]
    public void NonExecutableConfigurationDisablesOnlyAgentPath(
        string mode,
        string? teamsProfile,
        string? apiProfile)
    {
        var state = AgentAnalysisExecutionState.Create(new AgentAnalysisExecutionOptions
        {
            Mode = mode,
            TeamsWorkflowProfile = teamsProfile,
            ApiProfile = apiProfile
        });
        var orchestrator = new AgentAnalysisOrchestrator(state, MicrosoftSourceCatalog.CreateDefault());

        Assert.False(state.IsEnabled);
        Assert.False(string.IsNullOrWhiteSpace(state.DisabledReason));
        Assert.Throws<AgentAnalysisUnavailableException>(() => orchestrator.Prepare(CreateSnapshot()));
    }

    [Fact]
    public void ApprovedApiProfileStillHonorsReleaseGateWithoutExposingConfigurationValues()
    {
        var profiles = new AgentAnalysisProfilesOptions
        {
            Apis =
            {
                ["foundry-prod"] = new ApiExecutionProfile
                {
                    Approved = true,
                    Provider = "AzureAIFoundry",
                    Deployment = "approved-deployment",
                    ApiVersion = "approved-version",
                    Authentication = "ManagedIdentity",
                    Region = "westeurope",
                    EndpointConfigurationKey = "AgentApi:Endpoint",
                    CredentialConfigurationKey = "AgentApi:Credential"
                }
            }
        };
        var state = AgentAnalysisExecutionState.Create(
            new AgentAnalysisExecutionOptions
            {
                Mode = AgentAnalysisExecutionModes.Api,
                ApiProfile = "foundry-prod"
            },
            profiles,
            _ => true);

        Assert.False(state.IsEnabled);
        Assert.Equal("foundry-prod", state.Profile);
        Assert.Contains("preparation only", state.DisabledReason, StringComparison.Ordinal);
        Assert.Contains("separate provider or Teams workflow approval", state.DisabledReason, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentApi:Credential", state.DisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ApprovedTeamsProfileStillRequiresEndpointAvailabilityAndSeparateApproval()
    {
        var profiles = new AgentAnalysisProfilesOptions
        {
            TeamsWorkflows =
            {
                ["teams-future"] = new TeamsWorkflowExecutionProfile
                {
                    Approved = true,
                    EndpointConfigurationKey = "AgentTeams:Endpoint"
                }
            }
        };
        var state = AgentAnalysisExecutionState.Create(
            new AgentAnalysisExecutionOptions
            {
                Mode = AgentAnalysisExecutionModes.TeamsWorkflow,
                TeamsWorkflowProfile = "teams-future"
            },
            profiles,
            _ => true);

        Assert.False(state.IsEnabled);
        Assert.Equal("teams-future", state.Profile);
        Assert.Contains("preparation only", state.DisabledReason, StringComparison.Ordinal);
        Assert.Contains("endpoint is available", state.DisabledReason, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentTeams:Endpoint", state.DisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingProtectedApiConfigurationFailsClosed()
    {
        var profiles = new AgentAnalysisProfilesOptions
        {
            Apis =
            {
                ["foundry-prod"] = new ApiExecutionProfile
                {
                    Approved = true,
                    Provider = "AzureAIFoundry",
                    Deployment = "approved-deployment",
                    ApiVersion = "approved-version",
                    Authentication = "ManagedIdentity",
                    Region = "westeurope",
                    EndpointConfigurationKey = "AgentApi:Endpoint",
                    CredentialConfigurationKey = "AgentApi:Credential"
                }
            }
        };

        var state = AgentAnalysisExecutionState.Create(
            new AgentAnalysisExecutionOptions
            {
                Mode = AgentAnalysisExecutionModes.Api,
                ApiProfile = "foundry-prod"
            },
            profiles,
            key => key == "AgentApi:Endpoint");

        Assert.False(state.IsEnabled);
        Assert.Contains("missing protected endpoint or credential", state.DisabledReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(DisabledTeamsWorkflowAdapter), AgentAnalysisExecutionModes.TeamsWorkflow)]
    [InlineData(typeof(DisabledApiAnalysisAdapter), AgentAnalysisExecutionModes.Api)]
    public async Task PlaceholderAdaptersFailClosedWithoutTransport(
        Type adapterType,
        string expectedMode)
    {
        var adapter = Assert.IsAssignableFrom<IAgentAnalysisExecutionAdapter>(
            Activator.CreateInstance(adapterType));
        var request = new AgentAnalysisAdapterRequest(
            Guid.NewGuid(),
            1,
            IncidentAnalysisSnapshot.CurrentVersion,
            AgentAnalysisResultContract.CurrentVersion,
            "{}");

        Assert.Equal(expectedMode, adapter.Mode);
        await Assert.ThrowsAsync<AgentAnalysisUnavailableException>(() =>
            adapter.ExecuteAsync(request));
    }

    private static IncidentAnalysisSnapshot CreateSnapshot() => new(
        new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero),
        new IncidentAnalysisScope(IncidentAnalysisScopeType.Incident, "test"),
        new DateTimeOffset(2026, 9, 16, 11, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero),
        100,
        120_000,
        [],
        [],
        [],
        [],
        new IncidentGraphContext(AlertGraphLayer.Domain, AlertGraphLayer.Site, AlertGraphLayer.Target, "Primary path", []),
        [],
        [],
        [],
        [],
        [],
        []);
}