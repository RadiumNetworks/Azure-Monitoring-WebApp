using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitoringApp.Tests;

internal static class TestCaseLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    static TestCaseLoader()
    {
        Options.Converters.Add(new JsonStringEnumConverter());
    }

    public static T Load<T>(string fileName) where T : class =>
        JsonSerializer.Deserialize<T>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestCases", fileName)),
            Options)
        ?? throw new InvalidOperationException($"Test case file '{fileName}' is empty.");
}