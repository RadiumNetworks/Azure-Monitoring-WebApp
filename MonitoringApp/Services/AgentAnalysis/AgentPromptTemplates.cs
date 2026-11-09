using System.Reflection;
using System.Text;

namespace MonitoringApp;

internal static class AgentPromptTemplates
{
    private const string Prefix = "MonitoringApp.AgentPrompts.";
    private static readonly Lazy<EnglishPromptTemplate> Snapshot = new(() =>
        EnglishPromptTemplate.Load(typeof(AgentPromptTemplates).Assembly, Prefix + "incident-analysis-v4.en.txt",
            "PROMPT_CONTRACT_VERSION", "RESULT_JSON_SHAPE"));
    private static readonly Lazy<EnglishPromptTemplate> Legacy = new(() =>
        EnglishPromptTemplate.Load(typeof(AgentPromptTemplates).Assembly, Prefix + "alert-triage-v1.en.txt"));
    private static readonly Lazy<EnglishPromptTemplate> EmptySources = new(() =>
        EnglishPromptTemplate.Load(typeof(AgentPromptTemplates).Assembly, Prefix + "microsoft-sources-empty-v1.en.txt"));
    private static readonly Lazy<EnglishPromptTemplate> RcaPreamble = new(() =>
        EnglishPromptTemplate.Load(typeof(AgentPromptTemplates).Assembly, Prefix + "rca-preamble-v1.en.txt", "INCIDENT_KIND"));
    private static readonly Lazy<EnglishPromptTemplate> RcaNoAlerts = new(() =>
        EnglishPromptTemplate.Load(typeof(AgentPromptTemplates).Assembly, Prefix + "rca-no-alerts-v1.en.txt"));

    internal static EnglishPromptTemplate SnapshotInstructions => Snapshot.Value;
    internal static EnglishPromptTemplate LegacyInstructions => Legacy.Value;
    internal static EnglishPromptTemplate EmptySourceInstructions => EmptySources.Value;
    internal static EnglishPromptTemplate RcaInstructions => RcaPreamble.Value;
    internal static EnglishPromptTemplate RcaNoAlertInstructions => RcaNoAlerts.Value;

    internal static void ValidatePackaged()
    {
        _ = SnapshotInstructions;
        _ = LegacyInstructions;
        _ = EmptySourceInstructions;
        _ = RcaInstructions;
        _ = RcaNoAlertInstructions;
    }
}

internal sealed class EnglishPromptTemplate
{
    private readonly string resourceName;
    private readonly (string Literal, string? Parameter)[] parts;
    private readonly HashSet<string> parameters;

    private EnglishPromptTemplate(string resourceName, (string Literal, string? Parameter)[] parts, HashSet<string> parameters)
    {
        this.resourceName = resourceName;
        this.parts = parts;
        this.parameters = parameters;
    }

    internal static EnglishPromptTemplate Load(Assembly assembly, string resourceName, params string[] requiredParameters)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The English prompt template '{resourceName}' is missing.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
        string content;
        try
        {
            content = reader.ReadToEnd();
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidOperationException($"The English prompt template '{resourceName}' is not valid UTF-8.", exception);
        }
        return Parse(resourceName, content, requiredParameters);
    }

    internal static EnglishPromptTemplate Parse(string resourceName, string content, params string[] requiredParameters)
    {
        content = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(content) || !content.EndsWith('\n') ||
            content.Contains('\r') || content.Contains('\0') || content.StartsWith('\uFEFF'))
            throw new InvalidOperationException($"The English prompt template '{resourceName}' has invalid text or line endings.");
        var expected = new HashSet<string>(requiredParameters, StringComparer.Ordinal);
        if (expected.Count != requiredParameters.Length || expected.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException($"The English prompt template '{resourceName}' has an invalid parameter contract.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var parts = new List<(string Literal, string? Parameter)>();
        var position = 0;
        while (position < content.Length)
        {
            var start = content.IndexOf("{{", position, StringComparison.Ordinal);
            var end = content.IndexOf("}}", position, StringComparison.Ordinal);
            if (end >= 0 && (start < 0 || end < start) || start >= 0 && end < 0)
                throw new InvalidOperationException($"The English prompt template '{resourceName}' has an unbalanced parameter marker.");
            if (start < 0)
            {
                parts.Add((content[position..], null));
                break;
            }
            var parameter = content[(start + 2)..end];
            if (!expected.Contains(parameter) || !seen.Add(parameter))
                throw new InvalidOperationException($"The English prompt template '{resourceName}' has an unknown or repeated parameter '{parameter}'.");
            parts.Add((content[position..start], parameter));
            position = end + 2;
        }
        if (!expected.SetEquals(seen))
            throw new InvalidOperationException($"The English prompt template '{resourceName}' is missing a required parameter.");
        return new(resourceName, parts.ToArray(), expected);
    }

    internal string Render(string newLine, params (string Name, string Value)[] values)
    {
        if (newLine is not ("\n" or "\r\n"))
            throw new InvalidOperationException($"The English prompt template '{resourceName}' requires LF or CRLF output.");
        var supplied = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value) || !supplied.TryAdd(name, value))
                throw new InvalidOperationException($"The English prompt template '{resourceName}' received an empty or repeated parameter.");
        if (!parameters.SetEquals(supplied.Keys))
            throw new InvalidOperationException($"The English prompt template '{resourceName}' received an incorrect parameter set.");
        var output = new StringBuilder();
        foreach (var (literal, parameter) in parts)
        {
            output.Append(literal.Replace("\n", newLine, StringComparison.Ordinal));
            if (parameter is not null) output.Append(supplied[parameter]);
        }
        return output.ToString();
    }
}
