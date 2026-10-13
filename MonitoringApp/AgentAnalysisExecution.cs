using System.Text.RegularExpressions;

namespace MonitoringApp;

public static class AgentAnalysisExecutionModes
{
    public const string CopilotPrompt = "CopilotPrompt";
    public const string TeamsWorkflow = "TeamsWorkflow";
    public const string Api = "Api";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [CopilotPrompt, TeamsWorkflow, Api],
        StringComparer.OrdinalIgnoreCase);
}

public sealed class AgentAnalysisExecutionOptions
{
    public const string SectionName = "AgentAnalysisExecution";
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string Mode { get; set; } = AgentAnalysisExecutionModes.CopilotPrompt;
    public string? TeamsWorkflowProfile { get; set; }
    public string? ApiProfile { get; set; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Version != CurrentVersion)
        {
            errors.Add($"Version must be {CurrentVersion}.");
        }

        if (!AgentAnalysisExecutionModes.All.Contains(Mode))
        {
            errors.Add($"Mode must be one of: {string.Join(", ", AgentAnalysisExecutionModes.All)}.");
            return errors;
        }

        if (Mode.Equals(AgentAnalysisExecutionModes.CopilotPrompt, StringComparison.OrdinalIgnoreCase))
        {
            RejectUnexpectedProfile(TeamsWorkflowProfile, nameof(TeamsWorkflowProfile), errors);
            RejectUnexpectedProfile(ApiProfile, nameof(ApiProfile), errors);
        }
        else if (Mode.Equals(AgentAnalysisExecutionModes.TeamsWorkflow, StringComparison.OrdinalIgnoreCase))
        {
            ValidateRequiredProfile(TeamsWorkflowProfile, nameof(TeamsWorkflowProfile), errors);
            RejectUnexpectedProfile(ApiProfile, nameof(ApiProfile), errors);
        }
        else
        {
            ValidateRequiredProfile(ApiProfile, nameof(ApiProfile), errors);
            RejectUnexpectedProfile(TeamsWorkflowProfile, nameof(TeamsWorkflowProfile), errors);
        }

        return errors;
    }

    private static void ValidateRequiredProfile(
        string? profile,
        string propertyName,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            errors.Add($"{propertyName} is required for the selected mode.");
            return;
        }

        if (!Regex.IsMatch(profile, "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant))
        {
            errors.Add($"{propertyName} must be a logical profile name of at most 64 characters; URLs and paths are not allowed.");
        }
    }

    private static void RejectUnexpectedProfile(
        string? profile,
        string propertyName,
        ICollection<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(profile))
        {
            errors.Add($"{propertyName} is not allowed for the selected mode.");
        }
    }
}

public sealed record AgentAnalysisExecutionState(
    int Version,
    string Mode,
    string? Profile,
    bool IsEnabled,
    string? DisabledReason)
{
    public static AgentAnalysisExecutionState Create(
        AgentAnalysisExecutionOptions options,
        AgentAnalysisProfilesOptions? profiles = null,
        Func<string, bool>? hasProtectedConfiguration = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = options.Validate();
        var profile = options.Mode.Equals(AgentAnalysisExecutionModes.TeamsWorkflow, StringComparison.OrdinalIgnoreCase)
            ? options.TeamsWorkflowProfile
            : options.Mode.Equals(AgentAnalysisExecutionModes.Api, StringComparison.OrdinalIgnoreCase)
                ? options.ApiProfile
                : null;
        if (errors.Count > 0)
        {
            return new(options.Version, options.Mode, profile, false, string.Join(" ", errors));
        }

        if (!options.Mode.Equals(AgentAnalysisExecutionModes.CopilotPrompt, StringComparison.OrdinalIgnoreCase))
        {
            var profileError = ResolveProfileError(options, profile, profiles, hasProtectedConfiguration);
            return new(
                options.Version,
                options.Mode,
                profile,
                false,
                profileError ??
                $"Mode '{options.Mode}' with profile '{profile}' is configured for preparation only. " +
                "External execution remains disabled until an endpoint is available and a separate provider or Teams workflow approval has been granted.");
        }

        return new(options.Version, AgentAnalysisExecutionModes.CopilotPrompt, null, true, null);
    }

    private static string? ResolveProfileError(
        AgentAnalysisExecutionOptions options,
        string? profileName,
        AgentAnalysisProfilesOptions? profiles,
        Func<string, bool>? hasProtectedConfiguration)
    {
        if (profiles is null || string.IsNullOrWhiteSpace(profileName))
        {
            return $"Profile '{profileName}' is not present in the server-side approved profile registry.";
        }

        var profileErrors = profiles.Validate();
        if (profileErrors.Count > 0)
        {
            return $"The server-side profile registry is invalid: {string.Join(" ", profileErrors)}";
        }

        if (options.Mode.Equals(AgentAnalysisExecutionModes.TeamsWorkflow, StringComparison.OrdinalIgnoreCase))
        {
            if (!profiles.TryGetTeamsWorkflow(profileName, out var teamsProfile))
            {
                return $"Teams workflow profile '{profileName}' is unknown.";
            }

            if (!teamsProfile.Approved)
            {
                return $"Teams workflow profile '{profileName}' is not approved.";
            }

            if (hasProtectedConfiguration is not null && !hasProtectedConfiguration(teamsProfile.EndpointConfigurationKey))
            {
                return $"Teams workflow profile '{profileName}' is missing protected endpoint configuration.";
            }
        }
        else
        {
            if (!profiles.TryGetApi(profileName, out var apiProfile))
            {
                return $"API profile '{profileName}' is unknown.";
            }

            if (!apiProfile.Approved)
            {
                return $"API profile '{profileName}' is not approved.";
            }

            if (hasProtectedConfiguration is not null &&
                (!hasProtectedConfiguration(apiProfile.EndpointConfigurationKey) ||
                 !hasProtectedConfiguration(apiProfile.CredentialConfigurationKey)))
            {
                return $"API profile '{profileName}' is missing protected endpoint or credential configuration.";
            }
        }

        return null;
    }
}

public sealed record AgentAnalysisPreparation(
    string Mode,
    string? Profile,
    string PromptVersion,
    string ResultContractVersion,
    string Prompt);

public sealed class AgentAnalysisUnavailableException(string message) : InvalidOperationException(message);

public sealed class AgentAnalysisOrchestrator(
    AgentAnalysisExecutionState executionState,
    MicrosoftSourceCatalog sourceCatalog)
{
    public AgentAnalysisExecutionState ExecutionState => executionState;

    public AgentAnalysisPreparation Prepare(IncidentAnalysisSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!executionState.IsEnabled)
        {
            throw new AgentAnalysisUnavailableException(
                executionState.DisabledReason ?? "Agent analysis is disabled by configuration.");
        }

        if (!executionState.Mode.Equals(AgentAnalysisExecutionModes.CopilotPrompt, StringComparison.OrdinalIgnoreCase))
        {
            throw new AgentAnalysisUnavailableException(
                $"No approved adapter is registered for mode '{executionState.Mode}'.");
        }

        return new AgentAnalysisPreparation(
            executionState.Mode,
            executionState.Profile,
            AlertAgentPrompt.SnapshotPromptVersion,
            AgentAnalysisResultContract.CurrentVersion,
            AlertAgentPrompt.Build(snapshot, sourceCatalog));
    }
}