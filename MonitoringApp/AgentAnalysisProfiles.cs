using System.Text.RegularExpressions;

namespace MonitoringApp;

public sealed class AgentAnalysisProfilesOptions
{
    public const string SectionName = "AgentAnalysisProfiles";

    public Dictionary<string, TeamsWorkflowExecutionProfile> TeamsWorkflows { get; set; } = [];
    public Dictionary<string, ApiExecutionProfile> Apis { get; set; } = [];

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        foreach (var (name, profile) in TeamsWorkflows)
        {
            ValidateProfileName(name, errors);
            if (string.IsNullOrWhiteSpace(profile.EndpointConfigurationKey))
            {
                errors.Add($"Teams workflow profile '{name}' requires EndpointConfigurationKey.");
            }
        }

        foreach (var (name, profile) in Apis)
        {
            ValidateProfileName(name, errors);
            if (string.IsNullOrWhiteSpace(profile.Provider) ||
                string.IsNullOrWhiteSpace(profile.Deployment) ||
                string.IsNullOrWhiteSpace(profile.ApiVersion) ||
                string.IsNullOrWhiteSpace(profile.Authentication) ||
                string.IsNullOrWhiteSpace(profile.Region) ||
                string.IsNullOrWhiteSpace(profile.EndpointConfigurationKey) ||
                string.IsNullOrWhiteSpace(profile.CredentialConfigurationKey))
            {
                errors.Add($"API profile '{name}' requires provider, deployment, API version, authentication, region, endpoint key, and credential key.");
            }

            if (profile.TimeoutSeconds is < 1 or > 300 ||
                profile.MaxRetries is < 0 or > 5 ||
                profile.MaxParallelism is < 1 or > 10 ||
                profile.MonthlyCostLimit <= 0)
            {
                errors.Add($"API profile '{name}' has invalid timeout, retry, parallelism, or cost limits.");
            }
        }

        return errors;
    }

    public bool TryGetTeamsWorkflow(string name, out TeamsWorkflowExecutionProfile profile) =>
        TryGet(TeamsWorkflows, name, out profile);

    public bool TryGetApi(string name, out ApiExecutionProfile profile) =>
        TryGet(Apis, name, out profile);

    private static bool TryGet<T>(IReadOnlyDictionary<string, T> profiles, string name, out T profile)
        where T : class
    {
        var match = profiles.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
        profile = match.Value!;
        return match.Key is not null;
    }

    private static void ValidateProfileName(string name, ICollection<string> errors)
    {
        if (!Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant))
        {
            errors.Add($"Profile name '{name}' is not a valid logical name.");
        }
    }
}

public sealed class TeamsWorkflowExecutionProfile
{
    public bool Approved { get; set; }
    public string EndpointConfigurationKey { get; set; } = string.Empty;
}

public sealed class ApiExecutionProfile
{
    public bool Approved { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Deployment { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = string.Empty;
    public string Authentication { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string EndpointConfigurationKey { get; set; } = string.Empty;
    public string CredentialConfigurationKey { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 60;
    public int MaxRetries { get; set; } = 2;
    public int MaxParallelism { get; set; } = 2;
    public decimal MonthlyCostLimit { get; set; } = 100;
}