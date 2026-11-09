using System.Text;
using System.Text.Json;

namespace MonitoringApp;

/// <summary>
/// Selects which alert fields are included in a generated incident-triage prompt. All optional sections are enabled by default.
/// </summary>
public sealed record AlertAgentPromptOptions(
    bool IncludeTargets = true,
    bool IncludeSearchQueries = true,
    bool IncludeSearchResults = true,
    bool IncludeDescriptions = true,
    bool IncludeComments = true);

/// <summary>
/// Creates a structured prompt from a snapshot of monitoring alerts. Alert content is clearly treated as untrusted data rather than instructions.
/// </summary>
public static class AlertAgentPrompt
{
    public const string SnapshotPromptVersion = "incident-analysis-v4";

    /// <summary>
    /// Builds the controlled Release A handoff from the complete incident snapshot.
    /// </summary>
    public static string Build(
        IncidentAnalysisSnapshot snapshot,
        MicrosoftSourceCatalog? sourceCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        sourceCatalog ??= MicrosoftSourceCatalog.CreateDefault();

        var prompt = new StringBuilder();
        prompt.Append(AgentPromptTemplates.SnapshotInstructions.Render(Environment.NewLine,
            ("PROMPT_CONTRACT_VERSION", SnapshotPromptVersion),
            ("RESULT_JSON_SHAPE", AgentAnalysisResultContract.JsonShape)));
        prompt.AppendLine();
        AppendMicrosoftReferences(prompt, sourceCatalog);
        prompt.AppendLine();
        prompt.AppendLine($"Snapshot generated: {snapshot.GeneratedAt:O}");
        prompt.AppendLine($"Snapshot scope: {snapshot.Scope.Type} / {Normalize(snapshot.Scope.Value)}");
        prompt.AppendLine($"Evidence counts: primary={snapshot.PrimaryEvidence.Count}, systemHistory={snapshot.SystemHistory.Count}, context24Hours={snapshot.Context24Hours.Count}, lifecycles={snapshot.Lifecycles.Count}");
        prompt.AppendLine("BEGIN_UNTRUSTED_INCIDENT_SNAPSHOT_JSON");
        prompt.AppendLine(snapshot.ToCanonicalJson());
        prompt.AppendLine("END_UNTRUSTED_INCIDENT_SNAPSHOT_JSON");
        return prompt.ToString().TrimEnd();
    }

    /// <summary>
    /// Builds an incident-triage prompt from the supplied alerts and field-selection options. The generated timestamp and alert count are included for context.
    /// </summary>
    public static string Build(
        IEnumerable<AlertRecord> alerts,
        DateTimeOffset generatedAt,
        AlertAgentPromptOptions options,
        QueryResultPresenter? queryResultPresenter = null)
    {
        var selectedAlerts = alerts.ToArray();
        var prompt = new StringBuilder();

        prompt.Append(AgentPromptTemplates.LegacyInstructions.Render(Environment.NewLine));
        prompt.AppendLine();
        AppendMicrosoftReferences(prompt, MicrosoftSourceCatalog.CreateDefault());
        prompt.AppendLine($"Snapshot generated: {generatedAt:O}");
        prompt.AppendLine($"Selected alerts: {selectedAlerts.Length}");

        foreach (var alert in selectedAlerts)
        {
            prompt.AppendLine();
            prompt.AppendLine($"Alert: {Normalize(alert.Name)}");
            prompt.AppendLine($"- Severity: {Normalize(alert.Severity)}");
            prompt.AppendLine($"- Condition: {Normalize(alert.MonitorCondition)}");
            prompt.AppendLine($"- Received (UTC): {alert.ReceivedAt.ToUniversalTime():O}");

            if (options.IncludeTargets)
            {
                var identity = queryResultPresenter?.ResolveIdentity(alert) ?? alert.DisplayIdentity;
                var targetDisplayName = identity is null || string.IsNullOrWhiteSpace(identity.SiteName)
                    ? identity?.TargetName ?? alert.TargetName
                    : $"{identity.TargetName} ({identity.SiteName})";
                prompt.AppendLine($"- Target: {Normalize(targetDisplayName)}");
            }

            if (options.IncludeSearchQueries && !string.IsNullOrWhiteSpace(alert.SearchQuery))
            {
                AppendMultiline(prompt, "Search query", alert.SearchQuery);
            }

            if (options.IncludeSearchResults && !string.IsNullOrWhiteSpace(alert.SearchResultsUrl))
            {
                AppendMultiline(prompt, "Search result", alert.SearchResultsUrl);
            }

            if (options.IncludeDescriptions && !string.IsNullOrWhiteSpace(alert.Description))
            {
                prompt.AppendLine($"- Description: {Normalize(alert.Description)}");
            }

            if (options.IncludeComments && !string.IsNullOrWhiteSpace(alert.Comments))
            {
                prompt.AppendLine($"- Operator comments: {Normalize(alert.Comments)}");
            }

            if (options.IncludeSearchResults && queryResultPresenter?.Parse(alert.RawJson) is { } queryResult)
            {
                AppendDiagnosticEvidence(prompt, queryResult);
            }
        }

        return prompt.ToString().TrimEnd();
    }

    private static void AppendMicrosoftReferences(
        StringBuilder prompt,
        MicrosoftSourceCatalog sourceCatalog)
    {
        prompt.AppendLine($"Approved Microsoft source catalog: {sourceCatalog.Version} ({sourceCatalog.SearchMode})");
        if (sourceCatalog.ApprovedSources.Count == 0)
        {
            prompt.Append(AgentPromptTemplates.EmptySourceInstructions.Render(Environment.NewLine));
            return;
        }

        foreach (var source in sourceCatalog.ApprovedSources)
        {
            prompt.AppendLine($"- sourceId={source.Id}; title={source.Title}; product={source.Product}; topics={string.Join(", ", source.Topics)}; symptoms={string.Join(", ", source.SymptomsOrErrorCodes)}; lastChecked={source.LastCheckedOn:yyyy-MM-dd}; url={source.Url.AbsoluteUri}");
        }
    }

    /// <summary>
    /// Collapses repeated whitespace into single spaces for compact prompt fields. This keeps user-provided values on one readable line.
    /// </summary>
    private static string Normalize(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Appends a labeled multi-line value while preserving its line structure. Each content line is indented below the label.
    /// </summary>
    private static void AppendMultiline(StringBuilder prompt, string label, string value)
    {
        prompt.AppendLine($"- {label}:");
        foreach (var line in value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            prompt.AppendLine($"  {line.TrimEnd()}");
        }
    }

    private static void AppendDiagnosticEvidence(StringBuilder prompt, QueryResultPresentation presentation)
    {
        prompt.AppendLine($"- Diagnostic evidence: {Normalize(presentation.Label)}");
        if (presentation.Summary.Count > 0)
        {
            prompt.AppendLine($"  - Summary: {string.Join(", ", presentation.Summary.Select(badge => Normalize(badge.Text)))}");
        }

        foreach (var row in presentation.Rows)
        {
            prompt.AppendLine($"  - Result: {Normalize(row.Title)}");
            AppendDiagnosticValues(prompt, row.Metadata.Select(value => (value.Label, value.Value)));
            AppendDiagnosticValues(prompt, row.Alerts.Select(value => (value.Label, value.Value)));
            AppendDiagnosticValues(prompt, row.Facts.Select(value => (value.Label, value.Value)));
            foreach (var details in row.Details)
            {
                prompt.AppendLine($"    - {Normalize(details.Label)}:");
                AppendDiagnosticValues(prompt, details.Items.Select(value => (value.Label, value.Value)), "      ");
            }
        }
    }

    private static void AppendDiagnosticValues(
        StringBuilder prompt,
        IEnumerable<(string Label, string Value)> values,
        string indent = "    ")
    {
        foreach (var (label, value) in values)
        {
            var normalizedLabel = string.IsNullOrWhiteSpace(label) ? "Value" : Normalize(label);
            prompt.AppendLine($"{indent}- {normalizedLabel}: {FormatDiagnosticValue(value)}");
        }
    }

    private static string FormatDiagnosticValue(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return string.Join(", ", document.RootElement.EnumerateObject()
                    .Select(property => $"{Normalize(property.Name)}={Normalize(property.Value.ToString())}"));
            }
        }
        catch (JsonException)
        {
        }

        return Normalize(value);
    }
}