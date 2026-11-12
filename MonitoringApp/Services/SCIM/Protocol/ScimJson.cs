using System.Text.Json;

namespace MonitoringApp;

internal static class ScimJson
{
    public static void Schema(JsonElement value, string schema)
    {
        if (!Property(value, "schemas", out var schemas) || schemas.ValueKind != JsonValueKind.Array ||
            schemas.GetArrayLength() != 1 || schemas[0].ValueKind != JsonValueKind.String || schemas[0].GetString() != schema)
            throw Error("The request must declare exactly the supported SCIM schema.", "invalidSyntax");
    }
    public static void Object(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Error("A JSON object is required.", "invalidSyntax");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                throw Error("Duplicate or unsupported attribute.", "invalidPath");
    }
    public static bool Property(JsonElement value, string name, out JsonElement result)
    {
        foreach (var property in value.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { result = property.Value; return true; }
        result = default;
        return false;
    }
    public static string RequiredText(JsonElement value, string name, int maximum) =>
        Property(value, name, out var text) ? Text(text, maximum, true)! : throw Error($"{name} is required.");
    public static string? OptionalText(JsonElement value, string name, int maximum) =>
        Property(value, name, out var text) ? Text(text, maximum, false) : null;
    public static string? Text(JsonElement value, int maximum, bool required)
    {
        if (!required && value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw Error("A string attribute is required.");
        var result = value.GetString()!.Trim();
        if (result.Length > maximum || required && result.Length == 0) throw Error($"Attribute length must be {(required ? 1 : 0)} to {maximum}.");
        return result.Length == 0 ? null : result;
    }
    private static ScimProtocolException Error(string detail, string type = "invalidValue") => new(400, detail, type);
}
