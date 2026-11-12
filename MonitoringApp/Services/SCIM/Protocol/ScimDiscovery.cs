namespace MonitoringApp;

public static class ScimDiscovery
{
    public static ScimServiceProviderResponse Configuration() => new(
        [ScimSchemas.ServiceProvider], new(true), new(false), new(true, ScimUserProtocol.MaximumPageSize),
        new(false), new(false), new(true),
        [new("oauthbearertoken", "Provisioning bearer token", "A separately configured, tenant-bound SCIM token over HTTPS.", true)]);

    public static ScimResourceTypeResponse UserType(ScimOptions options) => new(
        [ScimSchemas.ResourceType], "User", "User", "/Users", ScimSchemas.User,
        new("ResourceType", $"{options.BaseUrl.TrimEnd('/')}/ResourceTypes/User"));
    public static ScimResourceTypeResponse GroupType(ScimOptions options) => new(
        [ScimSchemas.ResourceType], "Group", "Group", "/Groups", ScimSchemas.Group,
        new("ResourceType", $"{options.BaseUrl.TrimEnd('/')}/ResourceTypes/Group"));

    public static ScimSchemaResponse GroupSchema(ScimOptions options) => new(
        [ScimSchemas.Schema], ScimSchemas.Group, "Group", "Direct source-bound User members; nested groups are not supported.",
        [
            new("id", "string", Mutability: "readOnly", Uniqueness: "server"),
            new("externalId", "string", Required: true, Mutability: "immutable"),
            new("displayName", "string", Required: true),
            new("members", "complex", MultiValued: true, SubAttributes:
                [new("value", "string", Required: true, Mutability: "immutable"), new("type", "string", Mutability: "readOnly")])
        ], new("Schema", $"{options.BaseUrl.TrimEnd('/')}/Schemas/{ScimSchemas.Group}"));

    public static ScimSchemaResponse UserSchema(ScimOptions options) => new(
        [ScimSchemas.Schema], ScimSchemas.User, "User", "Supported Entra user profile; one primary work email.",
        [
            new("id", "string", Mutability: "readOnly", Uniqueness: "server"),
            new("externalId", "string", Required: true, Mutability: "immutable"),
            new("userName", "string", Required: true, Uniqueness: "server"),
            new("displayName", "string"),
            new("name", "complex", SubAttributes: [new("givenName", "string"), new("familyName", "string")]),
            new("emails", "complex", MultiValued: true, SubAttributes:
                [new("value", "string"), new("type", "string"), new("primary", "boolean")]),
            new("active", "boolean")
        ],
        new("Schema", $"{options.BaseUrl.TrimEnd('/')}/Schemas/{ScimSchemas.User}"));
}
