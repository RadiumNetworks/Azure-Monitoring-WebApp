namespace MonitoringApp.Tests;

public sealed class AgentAnalysisOrchestratorTests
{
    [Theory]
    [InlineData(0, AgentAnalysisExecutionDisabledCode.OptionsInvalid)]
    [InlineData(1, AgentAnalysisExecutionDisabledCode.ProfileRegistryMissing)]
    [InlineData(2, AgentAnalysisExecutionDisabledCode.ProfileRegistryInvalid)]
    [InlineData(3, AgentAnalysisExecutionDisabledCode.TeamsProfileUnknown)]
    [InlineData(4, AgentAnalysisExecutionDisabledCode.TeamsProfileNotApproved)]
    [InlineData(5, AgentAnalysisExecutionDisabledCode.TeamsConfigurationMissing)]
    [InlineData(6, AgentAnalysisExecutionDisabledCode.ApiProfileUnknown)]
    [InlineData(7, AgentAnalysisExecutionDisabledCode.ApiProfileNotApproved)]
    [InlineData(8, AgentAnalysisExecutionDisabledCode.ApiConfigurationMissing)]
    [InlineData(9, AgentAnalysisExecutionDisabledCode.PreparationOnly)]
    public void DisabledCodesRemainAdditiveAndPreserveServiceDiagnostics(int scenario, AgentAnalysisExecutionDisabledCode code)
    {
        var state = CreateDisabledState(scenario);
        Assert.False(state.IsEnabled);
        Assert.Equal(code, state.DisabledCode);
        var exception = Assert.Throws<AgentAnalysisUnavailableException>(() =>
            new AgentAnalysisOrchestrator(state, MicrosoftSourceCatalog.CreateDefault()).Prepare(CreateSnapshot()));
        Assert.Equal(state.DisabledReason, exception.Message);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(state with { DisabledCode = null }),
            System.Text.Json.JsonSerializer.Serialize(state));
    }

    internal static AgentAnalysisExecutionState CreateDisabledState(int scenario)
    {
        var options = new AgentAnalysisExecutionOptions { Mode = "TeamsWorkflow", TeamsWorkflowProfile = "teams-profile" };
        var profiles = new AgentAnalysisProfilesOptions();
        profiles.TeamsWorkflows["teams-profile"] = new()
            { Approved = true, EndpointConfigurationKey = "Hidden:Endpoint" };
        profiles.Apis["api-profile"] = new()
        {
            Approved = true, Provider = "AzureAIFoundry", Deployment = "deployment", ApiVersion = "version",
            Authentication = "ManagedIdentity", Region = "region", EndpointConfigurationKey = "Hidden:Endpoint",
            CredentialConfigurationKey = "Hidden:Credential"
        };
        if (scenario == 0) options.Mode = "Unknown";
        if (scenario == 2) profiles.TeamsWorkflows["teams-profile"].EndpointConfigurationKey = "";
        if (scenario == 3) options.TeamsWorkflowProfile = "missing-profile";
        if (scenario == 4) profiles.TeamsWorkflows["teams-profile"].Approved = false;
        if (scenario >= 6)
        {
            options.Mode = "Api";
            options.TeamsWorkflowProfile = null;
            options.ApiProfile = scenario == 6 ? "missing-profile" : "api-profile";
        }
        if (scenario == 7) profiles.Apis["api-profile"].Approved = false;
        return AgentAnalysisExecutionState.Create(options, scenario == 1 ? null : profiles,
            key => scenario != 5 && (scenario != 8 || key != "Hidden:Credential"));
    }

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