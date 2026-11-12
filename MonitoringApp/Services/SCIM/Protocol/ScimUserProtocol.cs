using System.Text.Json;
using System.Text.RegularExpressions;
using static MonitoringApp.ScimJson;

namespace MonitoringApp;

public static partial class ScimUserProtocol
{
    public const int MaximumPageSize = 100;

    public static ProvisioningFilter? Filter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return null;
        if (filter.Length > 1024) throw Error("The filter is too long.", "invalidFilter");
        var match = EqualityFilter().Match(filter);
        if (!match.Success) throw Error("Only userName eq and externalId eq filters are supported.", "invalidFilter");
        var attribute = match.Groups[1].Value.ToLowerInvariant();
        string value;
        try { value = JsonSerializer.Deserialize<string>(match.Groups[2].Value)!; }
        catch (JsonException) { throw Error("The filter value is invalid.", "invalidFilter"); }
        if (attribute == "externalid")
        {
            if (!Guid.TryParse(value, out var id) || id == Guid.Empty) throw Error("externalId must be an Entra Object ID.", "invalidFilter");
            value = id.ToString("D");
        }
        return new(attribute, value);
    }

    public static ProvisioningProfile Read(JsonElement body)
    {
        Object(body, "schemas", "id", "meta", "externalId", "userName", "displayName", "name", "emails", "active");
        Schema(body, ScimSchemas.User);
        if (!Property(body, "externalId", out var external) || external.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(external.GetString(), out var externalId) || externalId == Guid.Empty)
            throw Error("externalId must contain the stable Entra Object ID.");
        var userName = RequiredText(body, "userName", 256);
        var display = OptionalText(body, "displayName", 256) ?? "";
        var name = Property(body, "name", out var structured) ? ReadName(structured) : new ScimNameResponse(null, null);
        var email = Property(body, "emails", out var emails) ? ReadEmails(emails) : null;
        var active = !Property(body, "active", out var activeValue) || Boolean(activeValue);
        return new(externalId, userName, display, name.GivenName, name.FamilyName, email, active);
    }

    public static ProvisioningProfile Patch(ProvisioningProfile current, JsonElement body)
    {
        Object(body, "schemas", "Operations");
        Schema(body, ScimSchemas.Patch);
        if (!Property(body, "Operations", out var operations) || operations.ValueKind != JsonValueKind.Array ||
            operations.GetArrayLength() is < 1 or > 100)
            throw Error("Operations must contain 1 to 100 PATCH operations.");
        var result = current;
        foreach (var operation in operations.EnumerateArray())
        {
            Object(operation, "op", "path", "value");
            var op = RequiredText(operation, "op", 16).ToLowerInvariant();
            if (op is not ("add" or "replace" or "remove")) throw Error("Unsupported PATCH operation.", "invalidSyntax");
            var path = OptionalText(operation, "path", 256);
            var hasValue = Property(operation, "value", out var value);
            if (op != "remove" && !hasValue) throw Error("PATCH add/replace requires a value.");
            if (path is null)
            {
                if (op == "remove") throw Error("PATCH remove requires a path.", "noTarget");
                Object(value, "externalId", "userName", "displayName", "name", "emails", "active");
                foreach (var property in value.EnumerateObject())
                    result = Apply(result, property.Name, property.Value, op);
            }
            else result = Apply(result, path, value, op);
        }
        return result;
    }

    private static ProvisioningProfile Apply(ProvisioningProfile current, string path, JsonElement value, string operation)
    {
        var remove = operation == "remove";
        if (path.StartsWith(ScimSchemas.User + ":", StringComparison.OrdinalIgnoreCase))
            path = path[(ScimSchemas.User.Length + 1)..];
        switch (path.ToLowerInvariant())
        {
            case "externalid":
                if (remove || value.ValueKind != JsonValueKind.String ||
                    !Guid.TryParse(value.GetString(), out var external) || external != current.ExternalId)
                    throw Error("externalId cannot be changed.", "mutability");
                return current;
            case "username":
                if (remove) throw Error("userName is required.", "mutability");
                return current with { UserName = Text(value, 256, true)! };
            case "displayname":
                return current with { DisplayName = remove ? "" : Text(value, 256, false) ?? "" };
            case "name":
                if (remove || value.ValueKind == JsonValueKind.Null) return current with { GivenName = null, FamilyName = null };
                var name = ReadName(value);
                return current with
                {
                    GivenName = operation == "replace" || Property(value, "givenName", out _) ? name.GivenName : current.GivenName,
                    FamilyName = operation == "replace" || Property(value, "familyName", out _) ? name.FamilyName : current.FamilyName
                };
            case "name.givenname":
                return current with { GivenName = remove ? null : Text(value, 256, false) };
            case "name.familyname":
                return current with { FamilyName = remove ? null : Text(value, 256, false) };
            case "emails":
                return current with { Email = remove ? null : ReadEmails(value) };
            case "active":
                if (remove) throw Error("active cannot be removed.", "mutability");
                return current with { Active = Boolean(value) };
            default:
                var emailPath = WorkEmailPath().Match(path);
                if (!emailPath.Success) throw Error("Unsupported PATCH attribute or value filter.", "invalidPath");
                if (remove && current.Email is null) throw Error("No matching work email exists.", "noTarget");
                return current with
                {
                    Email = remove ? null : emailPath.Groups[1].Success
                        ? Text(value, 320, false) : value.ValueKind == JsonValueKind.Array ? ReadEmails(value) : ReadEmail(value)
                };
        }
    }

    private static ScimNameResponse ReadName(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return new(null, null);
        Object(value, "givenName", "familyName");
        return new(OptionalText(value, "givenName", 256), OptionalText(value, "familyName", 256));
    }

    private static string? ReadEmails(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 1)
            throw Error("emails supports at most one work email.");
        return value.GetArrayLength() == 0 ? null : ReadEmail(value[0]);
    }

    private static string? ReadEmail(JsonElement value)
    {
        Object(value, "type", "value", "primary");
        if (Property(value, "type", out var type) && !string.Equals(Text(type, 16, true), "work", StringComparison.OrdinalIgnoreCase))
            throw Error("Only the work email type is supported.");
        if (Property(value, "primary", out var primary) && !Boolean(primary))
            throw Error("The single work email must be primary.");
        return OptionalText(value, "value", 320);
    }

    private static bool Boolean(JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? value.GetBoolean() : throw Error("active/primary must be a JSON boolean.");

    private static ScimProtocolException Error(string detail, string type = "invalidValue") => new(400, detail, type);

    [GeneratedRegex("""^\s*(externalId|userName)\s+eq\s+("(?:[^"\\]|\\.)*")\s*$""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EqualityFilter();
    [GeneratedRegex("""^emails\s*\[\s*type\s+eq\s+"work"\s*\](\.value)?$""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WorkEmailPath();
}
