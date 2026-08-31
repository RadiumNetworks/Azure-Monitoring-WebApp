using System.Text.Json;
using System.Text.Json.Serialization;
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

    public static readonly IReadOnlyList<string> RequiredNames =
        [Authentication, AlertHistory, AlertGraph, AlertSeverityDisplay];
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
            command.CommandText = """
                SELECT [Name], [JsonValue]
                FROM [dbo].[Settings]
                WHERE [Name] IN (N'Authentication', N'AlertHistory', N'AlertGraph', N'AlertSeverityDisplay');
                """;
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

        var missing = RequiredNames.Where(name => !jsonByName.ContainsKey(name)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Required database settings are missing from dbo.Settings: {string.Join(", ", missing)}.");
        }

        return new DatabaseStartupSettings(
            Deserialize<ApplicationAuthenticationOptions>(Authentication, jsonByName[Authentication]),
            Deserialize<AlertHistoryOptions>(AlertHistory, jsonByName[AlertHistory]),
            Deserialize<AlertGraphOptions>(AlertGraph, jsonByName[AlertGraph]),
            Deserialize<AlertSeverityDisplayOptions>(AlertSeverityDisplay, jsonByName[AlertSeverityDisplay]));
    }

    public static T Deserialize<T>(string name, string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, SerializerOptions)
                ?? throw new InvalidOperationException($"Database setting '{name}' contains JSON null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Database setting '{name}' does not contain valid JSON for {typeof(T).Name}.",
                exception);
        }
    }
}

public sealed record DatabaseStartupSettings(
    ApplicationAuthenticationOptions Authentication,
    AlertHistoryOptions AlertHistory,
    AlertGraphOptions AlertGraph,
    AlertSeverityDisplayOptions AlertSeverityDisplay);
