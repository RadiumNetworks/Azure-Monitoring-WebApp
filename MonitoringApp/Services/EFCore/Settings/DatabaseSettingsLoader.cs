using System.Text.Json;
using System.Text.Json.Serialization;
using System.Data;
using Microsoft.Data.SqlClient;

namespace MonitoringApp;

public sealed class DatabaseSetting
{
    public string Name { get; set; } = string.Empty;
    public string JsonValue { get; set; } = string.Empty;
}

/// <summary>
/// Loads startup-critical application settings from SQL before the service container is built.
/// The database connection string itself remains in normal ASP.NET Core configuration.
/// </summary>
public static class DatabaseSettingsLoader
{
    public const string Authentication = ApplicationAuthenticationOptions.SectionName;
    public const string AlertHistory = AlertHistoryOptions.SectionName;
    public const string AlertGraph = AlertGraphOptions.SectionName;
    public const string AlertSeverityDisplay = AlertSeverityDisplayOptions.SectionName;
    public const string InboxTargetOutputDisplay = InboxTargetOutputDisplayOptions.SectionName;
    public const string RcaDisplay = RcaDisplayOptions.SectionName;
    public const string DashboardFilters = DashboardFilterOptions.SectionName;
    public const string AgentAnalysisContext = AgentAnalysisContextOptions.SectionName;
    public const string AgentAnalysisExecution = AgentAnalysisExecutionOptions.SectionName;

    public static readonly IReadOnlyList<string> RequiredNames =
        [Authentication, AlertHistory, AlertGraph, AlertSeverityDisplay, InboxTargetOutputDisplay, RcaDisplay, DashboardFilters, AgentAnalysisContext, AgentAnalysisExecution];
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static DatabaseStartupSettings LoadRequired(string connectionString)
    {
        var jsonByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var connection = new SqlConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            var parameters = RequiredNames.Select((_, index) => $"@name{index}").ToArray();
            command.CommandText = $"""
                SELECT [Name], [JsonValue]
                FROM [dbo].[Settings]
                WHERE [Name] IN ({string.Join(", ", parameters)});
                """;
            for (var index = 0; index < RequiredNames.Count; index++)
                command.Parameters.Add(parameters[index], SqlDbType.NVarChar, 128).Value = RequiredNames[index];
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                jsonByName[reader.GetString(0)] = reader.GetString(1);
            }
        }
        catch (SqlException exception)
        {
            throw new InvalidOperationException(
                "Application settings could not be loaded from dbo.Settings. Apply all database migrations before starting the application.",
                exception);
        }

        return ParseRequired(jsonByName);
    }

    public static void EnsureRequired(IEnumerable<string> names)
    {
        var existing = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = RequiredNames.Where(name => !existing.Contains(name)).ToArray();
        if (missing.Length > 0)
        {
            throw new DatabaseSettingsValidationException(DatabaseSettingsValidationCode.MissingSettings,
                $"Required database settings are missing from dbo.Settings: {string.Join(", ", missing)}.");
        }

    }

    public static DatabaseStartupSettings ParseRequired(IReadOnlyDictionary<string, string> jsonByName)
    {
        EnsureRequired(jsonByName.Keys);
        return new DatabaseStartupSettings(
            Deserialize<ApplicationAuthenticationOptions>(Authentication, jsonByName[Authentication]),
            Deserialize<AlertHistoryOptions>(AlertHistory, jsonByName[AlertHistory]),
            Deserialize<AlertGraphOptions>(AlertGraph, jsonByName[AlertGraph]),
            Deserialize<AlertSeverityDisplayOptions>(AlertSeverityDisplay, jsonByName[AlertSeverityDisplay]),
            Deserialize<AgentAnalysisContextOptions>(AgentAnalysisContext, jsonByName[AgentAnalysisContext]),
            Deserialize<AgentAnalysisExecutionOptions>(AgentAnalysisExecution, jsonByName[AgentAnalysisExecution]),
            Deserialize<InboxTargetOutputDisplayOptions>(InboxTargetOutputDisplay, jsonByName[InboxTargetOutputDisplay]),
            Deserialize<RcaDisplayOptions>(RcaDisplay, jsonByName[RcaDisplay]),
            Deserialize<DashboardFilterOptions>(DashboardFilters, jsonByName[DashboardFilters]));
    }

    public static T Deserialize<T>(string name, string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, SerializerOptions)
                ?? throw new DatabaseSettingsValidationException(DatabaseSettingsValidationCode.JsonNull,
                    $"Database setting '{name}' contains JSON null.", name);
        }
        catch (JsonException exception)
        {
            throw new DatabaseSettingsValidationException(DatabaseSettingsValidationCode.JsonInvalid,
                $"Database setting '{name}' does not contain valid JSON for {typeof(T).Name}.",
                name, exception);
        }
    }
}

public sealed record DatabaseStartupSettings(
    ApplicationAuthenticationOptions Authentication,
    AlertHistoryOptions AlertHistory,
    AlertGraphOptions AlertGraph,
    AlertSeverityDisplayOptions AlertSeverityDisplay,
    AgentAnalysisContextOptions AgentAnalysisContext,
    AgentAnalysisExecutionOptions AgentAnalysisExecution,
    InboxTargetOutputDisplayOptions InboxTargetOutputDisplay,
    RcaDisplayOptions RcaDisplay,
    DashboardFilterOptions DashboardFilters);
