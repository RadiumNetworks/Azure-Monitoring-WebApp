using System.Text.Json;

namespace MonitoringApp;

internal static class LockoutQueryTemplates
{
    internal static string Users() => Load("lockout-users.json");

    internal static string Events(string user)
    {
        if (string.IsNullOrWhiteSpace(user) || user.Length > 1024)
            throw new InvalidOperationException("Select a nonempty lockout user (maximum 1024 characters).");
        var template = Load("lockout-events.json");
        if (template.Split("{{User}}", StringSplitOptions.None).Length != 2)
            throw new InvalidOperationException("The lockout query must contain exactly one user parameter.");
        return template.Replace("{{User}}", JsonSerializer.Serialize(user), StringComparison.Ordinal);
    }

    private static string Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "AnalysisQueries", "Support", name);
        var template = JsonSerializer.Deserialize<Template>(File.ReadAllText(path), QueryTemplateJson.Options)
            ?? throw new InvalidOperationException("The lockout query template must be an object.");
        if (string.IsNullOrWhiteSpace(template.Kql))
            throw new InvalidOperationException("The lockout query template requires KQL.");
        return template.Kql;
    }

    private sealed record Template(string Kql);
}
