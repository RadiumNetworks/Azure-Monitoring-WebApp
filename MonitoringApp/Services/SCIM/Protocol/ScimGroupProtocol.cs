using System.Text.Json;
using System.Text.RegularExpressions;

namespace MonitoringApp;

public static partial class ScimGroupProtocol
{
    public static ProvisioningFilter? Filter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return null;
        if (filter.Length > 1024) throw Error("The filter is too long.", "invalidFilter");
        var match = EqualityFilter().Match(filter);
        if (!match.Success) throw Error("Only displayName eq and externalId eq filters are supported.", "invalidFilter");
        string value;
        try { value = JsonSerializer.Deserialize<string>(match.Groups[2].Value)!; }
        catch (JsonException) { throw Error("The filter value is invalid.", "invalidFilter"); }
        var attribute = match.Groups[1].Value.ToLowerInvariant();
        if (attribute == "externalid")
        {
            if (!Guid.TryParse(value, out var id) || id == Guid.Empty) throw Error("externalId must be an Entra Object ID.", "invalidFilter");
            value = id.ToString("D");
        }
        return new(attribute, value);
    }

    public static ProvisioningGroupProfile Read(JsonElement body)
    {
        ScimJson.Object(body, "schemas", "id", "meta", "externalId", "displayName", "members");
        ScimJson.Schema(body, ScimSchemas.Group);
        var external = Id(ScimJson.RequiredText(body, "externalId", 36));
        var display = ScimJson.RequiredText(body, "displayName", 256);
        var members = ScimJson.Property(body, "members", out var value) ? Members(value) : [];
        return new(external, display, members);
    }

    public static ProvisioningGroupProfile Patch(ProvisioningGroupProfile current, JsonElement body)
    {
        ScimJson.Object(body, "schemas", "Operations");
        ScimJson.Schema(body, ScimSchemas.Patch);
        if (!ScimJson.Property(body, "Operations", out var operations) || operations.ValueKind != JsonValueKind.Array ||
            operations.GetArrayLength() is < 1 or > 100)
            throw Error("Operations must contain 1 to 100 PATCH operations.");
        var result = current;
        foreach (var operation in operations.EnumerateArray())
        {
            ScimJson.Object(operation, "op", "path", "value");
            var op = ScimJson.RequiredText(operation, "op", 16).ToLowerInvariant();
            if (op is not ("add" or "replace" or "remove")) throw Error("Unsupported PATCH operation.", "invalidSyntax");
            var path = ScimJson.OptionalText(operation, "path", 512);
            var hasValue = ScimJson.Property(operation, "value", out var value);
            if (op != "remove" && !hasValue) throw Error("PATCH add/replace requires a value.");
            if (path is null)
            {
                if (op == "remove") throw Error("PATCH remove requires a path.", "noTarget");
                ScimJson.Object(value, "externalId", "displayName", "members");
                foreach (var property in value.EnumerateObject()) result = Apply(result, property.Name, property.Value, op);
            }
            else result = Apply(result, path, value, op);
        }
        return result;
    }

    private static ProvisioningGroupProfile Apply(ProvisioningGroupProfile current, string path, JsonElement value, string op)
    {
        if (path.StartsWith(ScimSchemas.Group + ":", StringComparison.OrdinalIgnoreCase)) path = path[(ScimSchemas.Group.Length + 1)..];
        switch (path.ToLowerInvariant())
        {
            case "externalid":
                if (op == "remove" || value.ValueKind != JsonValueKind.String || Id(value.GetString()!) != current.ExternalId)
                    throw Error("externalId cannot be changed.", "mutability");
                return current;
            case "displayname":
                if (op == "remove") throw Error("displayName is required.", "mutability");
                return current with { DisplayName = ScimJson.Text(value, 256, true)! };
            case "members":
                if (op == "remove" && value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return current with { Members = [] };
                var members = Members(value);
                return current with { Members = op switch
                {
                    "add" => current.Members.Union(members).Order().ToArray(),
                    "remove" => current.Members.Except(members).Order().ToArray(),
                    _ => members
                } };
            default:
                var match = MemberPath().Match(path);
                if (!match.Success) throw Error("Unsupported group PATCH path.", "invalidPath");
                if (op != "remove") throw Error("Filtered member paths support remove only.", "invalidPath");
                var id = Id(match.Groups[1].Value);
                return current with { Members = current.Members.Where(member => member != id).ToArray() };
        }
    }

    private static Guid[] Members(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return [];
        if (value.ValueKind != JsonValueKind.Array) throw Error("members must be an array.");
        return value.EnumerateArray().Select(member =>
        {
            ScimJson.Object(member, "value", "display", "type", "$ref");
            if (ScimJson.Property(member, "type", out var type) &&
                !string.Equals(ScimJson.Text(type, 16, true), "User", StringComparison.OrdinalIgnoreCase))
                throw Error("Only direct User members are supported.");
            if (ScimJson.Property(member, "display", out var display)) _ = ScimJson.Text(display, 256, false);
            if (ScimJson.Property(member, "$ref", out var reference)) _ = ScimJson.Text(reference, 2048, false);
            return Id(ScimJson.RequiredText(member, "value", 36));
        }).Distinct().Order().ToArray();
    }
    private static Guid Id(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty
        ? id : throw Error("A stable nonempty UUID is required.");
    private static ScimProtocolException Error(string detail, string type = "invalidValue") => new(400, detail, type);
    [GeneratedRegex("""^\s*(externalId|displayName)\s+eq\s+("(?:[^"\\]|\\.)*")\s*$""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EqualityFilter();
    [GeneratedRegex("""^members\s*\[\s*value\s+eq\s+"([0-9a-f-]{36})"\s*\]$""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MemberPath();
}
