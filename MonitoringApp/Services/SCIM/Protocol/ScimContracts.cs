using System.Text.Json.Serialization;

namespace MonitoringApp;

public static class ScimSchemas
{
    public const string User = "urn:ietf:params:scim:schemas:core:2.0:User";
    public const string Group = "urn:ietf:params:scim:schemas:core:2.0:Group";
    public const string List = "urn:ietf:params:scim:api:messages:2.0:ListResponse";
    public const string Patch = "urn:ietf:params:scim:api:messages:2.0:PatchOp";
    public const string Error = "urn:ietf:params:scim:api:messages:2.0:Error";
    public const string ServiceProvider = "urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig";
    public const string ResourceType = "urn:ietf:params:scim:schemas:core:2.0:ResourceType";
    public const string Schema = "urn:ietf:params:scim:schemas:core:2.0:Schema";
}

/// <summary>SCIM error response, using the protocol's string status rather than Problem Details.</summary>
public sealed record ScimErrorResponse(string[] Schemas, string Status, string Detail, string? ScimType = null);
/// <summary>SCIM paginated resource list.</summary>
public sealed record ScimListResponse<T>(
    string[] Schemas, int TotalResults, int StartIndex, int ItemsPerPage,
    [property: JsonPropertyName("Resources")] IReadOnlyList<T> Resources);
/// <summary>Source-provided structured user name.</summary>
public sealed record ScimNameResponse(string? GivenName, string? FamilyName);
/// <summary>The supported work email address.</summary>
public sealed record ScimEmailResponse(string Value, string Type = "work", bool Primary = true);
/// <summary>SCIM resource metadata and optimistic version.</summary>
public sealed record ScimMetaResponse(string ResourceType, string Location, DateTimeOffset? Created = null,
    DateTimeOffset? LastModified = null, string? Version = null);
/// <summary>A provisioned Entra user, without local roles, passwords or display overrides.</summary>
public sealed record ScimUserResponse(
    string[] Schemas, string Id, string ExternalId, string UserName, string DisplayName,
    ScimNameResponse Name, IReadOnlyList<ScimEmailResponse> Emails, bool Active, ScimMetaResponse Meta);
/// <summary>A direct User member, identified by its server-assigned SCIM User ID.</summary>
public sealed record ScimGroupMemberResponse(string Value, string Type = "User");
/// <summary>A provisioned group with direct user members; no application roles or automatic team creation.</summary>
public sealed record ScimGroupResponse(string[] Schemas, string Id, string ExternalId, string DisplayName,
    IReadOnlyList<ScimGroupMemberResponse> Members, ScimMetaResponse Meta);
/// <summary>A supported SCIM capability.</summary>
public sealed record ScimCapability(bool Supported);
/// <summary>Supported equality filtering and maximum page size.</summary>
public sealed record ScimFilterCapability(bool Supported, int MaxResults);
/// <summary>Bulk capability, disabled in this implementation.</summary>
public sealed record ScimBulkCapability(bool Supported, int MaxOperations = 0, int MaxPayloadSize = 0);
/// <summary>Machine authentication advertised by SCIM discovery.</summary>
public sealed record ScimAuthenticationResponse(string Type, string Name, string Description, bool Primary);
/// <summary>Implemented SCIM server capabilities.</summary>
public sealed record ScimServiceProviderResponse(
    string[] Schemas, ScimCapability Patch, ScimBulkCapability Bulk, ScimFilterCapability Filter,
    ScimCapability ChangePassword, ScimCapability Sort,
    [property: JsonPropertyName("etag")] ScimCapability ETag,
    ScimAuthenticationResponse[] AuthenticationSchemes);
/// <summary>A supported SCIM resource type.</summary>
public sealed record ScimResourceTypeResponse(string[] Schemas, string Id, string Name, string Endpoint,
    string Schema, ScimMetaResponse Meta);
/// <summary>A supported SCIM schema attribute.</summary>
public sealed record ScimAttributeResponse(string Name, string Type, bool MultiValued = false, bool Required = false,
    bool CaseExact = false, string Mutability = "readWrite", string Returned = "default", string Uniqueness = "none",
    ScimAttributeResponse[]? SubAttributes = null);
/// <summary>The supported user schema subset.</summary>
public sealed record ScimSchemaResponse(string[] Schemas, string Id, string Name, string Description,
    ScimAttributeResponse[] Attributes, ScimMetaResponse Meta);

public sealed class ScimProtocolException(int status, string detail, string? scimType = null) : InvalidOperationException(detail)
{
    public int Status { get; } = status;
    public string? ScimType { get; } = scimType;
}
