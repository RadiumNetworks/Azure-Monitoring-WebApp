using System.Text.Json;

namespace PlaywrightDemo.TestCases;

public static class TestCaseLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static T Load<T>(string fileName) where T : class =>
        JsonSerializer.Deserialize<T>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestCases", fileName)),
            SerializerOptions)
        ?? throw new InvalidOperationException($"Test case file '{fileName}' is empty or invalid.");
}