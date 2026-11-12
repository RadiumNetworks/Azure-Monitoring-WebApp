using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MonitoringApp;

public static class ScimApi
{
    public static void MapScimApi(this WebApplication app)
    {
        var group = app.MapGroup("/scim/v2").WithTags("SCIM provisioning")
            .AllowAnonymous().AddEndpointFilter<ScimRequestFilter>();

        Describe(group.MapGet("/Users", async (HttpContext http, IScimUserService service, CancellationToken cancellationToken) =>
        {
            var query = http.Request.Query;
            if (query.Keys.Any(key => key is not ("filter" or "startIndex" or "count")) || query.Any(pair => pair.Value.Count != 1))
                throw new ScimProtocolException(400, "Only filter, startIndex and count query parameters are supported.", "invalidFilter");
            var result = await service.ListAsync(ScimRequestFilter.Source(http), query["filter"].FirstOrDefault(),
                Number(http, "startIndex", 1), Number(http, "count", 100), cancellationToken);
            return Json(result);
        }), "ScimListUsers", "List SCIM users with matching and pagination.")
            .Produces<ScimListResponse<ScimUserResponse>>(200, ScimRequestFilter.ContentType);

        Describe(group.MapGet("/Users/{id}", async (string id, HttpContext http, IScimUserService service, CancellationToken cancellationToken) =>
            User(http, await service.GetAsync(ScimRequestFilter.Source(http), Id(id), cancellationToken))),
            "ScimGetUser", "Read a provisioned SCIM user.")
            .Produces<ScimUserResponse>(200, ScimRequestFilter.ContentType);

        Describe(group.MapPost("/Users", async (HttpContext http, IScimUserService service, CancellationToken cancellationToken) =>
        {
            using var body = await BodyAsync(http.Request, cancellationToken);
            var user = await service.CreateAsync(ScimRequestFilter.Source(http), body.RootElement, cancellationToken);
            http.Response.Headers.Location = user.Meta.Location;
            return User(http, user, 201);
        }), "ScimCreateUser", "Provision an Entra user without creating credentials or granting app roles.")
            .Produces<ScimUserResponse>(201, ScimRequestFilter.ContentType);

        Describe(group.MapPut("/Users/{id}", async (string id, HttpContext http, IScimUserService service, CancellationToken cancellationToken) =>
        {
            using var body = await BodyAsync(http.Request, cancellationToken);
            return User(http, await service.ReplaceAsync(ScimRequestFilter.Source(http), Id(id), body.RootElement,
                Version(http), cancellationToken));
        }), "ScimReplaceUser", "Replace the supported source profile; the stable external identity is immutable.")
            .Produces<ScimUserResponse>(200, ScimRequestFilter.ContentType);

        Describe(group.MapPatch("/Users/{id}", async (string id, HttpContext http, IScimUserService service, CancellationToken cancellationToken) =>
        {
            using var body = await BodyAsync(http.Request, cancellationToken);
            return User(http, await service.PatchAsync(ScimRequestFilter.Source(http), Id(id), body.RootElement,
                Version(http), cancellationToken));
        }), "ScimPatchUser", "Apply an atomic SCIM PATCH to profile attributes or active status.")
            .Produces<ScimUserResponse>(200, ScimRequestFilter.ContentType);

        Describe(group.MapDelete("/Users/{id}", async (string id, HttpContext http, IScimUserService service, CancellationToken cancellationToken) =>
        {
            await service.DeleteAsync(ScimRequestFilter.Source(http), Id(id), Version(http), cancellationToken);
            return TypedResults.NoContent();
        }), "ScimDeleteUser", "Deprovision a user without deleting app data, ownership or historical references.")
            .Produces(204);

        Describe(group.MapGet("/Groups", async (HttpContext http, IScimGroupService service, CancellationToken cancellationToken) =>
        {
            var query = http.Request.Query;
            if (query.Keys.Any(key => key is not ("filter" or "startIndex" or "count")) || query.Any(pair => pair.Value.Count != 1))
                throw new ScimProtocolException(400, "Only filter, startIndex and count query parameters are supported.", "invalidFilter");
            return Json(await service.ListAsync(ScimRequestFilter.Source(http), query["filter"].FirstOrDefault(),
                Number(http, "startIndex", 1), Number(http, "count", 100), cancellationToken));
        }), "ScimListGroups", "List source-bound groups with equality matching and pagination.")
            .Produces<ScimListResponse<ScimGroupResponse>>(200, ScimRequestFilter.ContentType);
        Describe(group.MapGet("/Groups/{id}", async (string id, HttpContext http, IScimGroupService service, CancellationToken cancellationToken) =>
            Group(http, await service.GetAsync(ScimRequestFilter.Source(http), Id(id), cancellationToken))),
            "ScimGetGroup", "Read a group and its direct User members.").Produces<ScimGroupResponse>(200, ScimRequestFilter.ContentType);
        Describe(group.MapPost("/Groups", async (HttpContext http, IScimGroupService service, CancellationToken cancellationToken) =>
        {
            using var body = await BodyAsync(http.Request, cancellationToken);
            var resource = await service.CreateAsync(ScimRequestFilter.Source(http), body.RootElement, cancellationToken);
            http.Response.Headers.Location = resource.Meta.Location;
            return Group(http, resource, 201);
        }), "ScimCreateGroup", "Provision a group without creating teams or granting app roles.")
            .Produces<ScimGroupResponse>(201, ScimRequestFilter.ContentType);
        Describe(group.MapPut("/Groups/{id}", async (string id, HttpContext http, IScimGroupService service, CancellationToken cancellationToken) =>
        {
            using var body = await BodyAsync(http.Request, cancellationToken);
            return Group(http, await service.ReplaceAsync(ScimRequestFilter.Source(http), Id(id), body.RootElement, Version(http), cancellationToken));
        }), "ScimReplaceGroup", "Replace a group's source name and complete direct membership set.")
            .Produces<ScimGroupResponse>(200, ScimRequestFilter.ContentType);
        Describe(group.MapPatch("/Groups/{id}", async (string id, HttpContext http, IScimGroupService service, CancellationToken cancellationToken) =>
        {
            using var body = await BodyAsync(http.Request, cancellationToken);
            return Group(http, await service.PatchAsync(ScimRequestFilter.Source(http), Id(id), body.RootElement, Version(http), cancellationToken));
        }), "ScimPatchGroup", "Atomically add, replace or remove direct members and reconcile mapped teams.")
            .Produces<ScimGroupResponse>(200, ScimRequestFilter.ContentType);
        Describe(group.MapDelete("/Groups/{id}", async (string id, HttpContext http, IScimGroupService service, CancellationToken cancellationToken) =>
        {
            await service.DeleteAsync(ScimRequestFilter.Source(http), Id(id), Version(http), cancellationToken);
            return TypedResults.NoContent();
        }), "ScimDeleteGroup", "Retain the group tombstone and mappings; remove only its derived memberships.").Produces(204);

        Describe(group.MapGet("/ServiceProviderConfig", () => Json(ScimDiscovery.Configuration())),
            "ScimServiceProviderConfig", "Read the implemented SCIM capabilities.")
            .Produces<ScimServiceProviderResponse>(200, ScimRequestFilter.ContentType);
        Describe(group.MapGet("/ResourceTypes", (ScimOptions options) => Json(List(ScimDiscovery.UserType(options), ScimDiscovery.GroupType(options)))),
            "ScimResourceTypes", "List supported User and Group resource types.")
            .Produces<ScimListResponse<ScimResourceTypeResponse>>(200, ScimRequestFilter.ContentType);
        Describe(group.MapGet("/ResourceTypes/{id}", (string id, ScimOptions options) =>
            id == "User" ? Json(ScimDiscovery.UserType(options)) : id == "Group" ? Json(ScimDiscovery.GroupType(options)) : throw Missing()),
            "ScimResourceType", "Read the supported User resource type.")
            .Produces<ScimResourceTypeResponse>(200, ScimRequestFilter.ContentType);
        Describe(group.MapGet("/Schemas", (ScimOptions options) => Json(List(ScimDiscovery.UserSchema(options), ScimDiscovery.GroupSchema(options)))),
            "ScimSchemas", "List supported schemas and profile attributes.")
            .Produces<ScimListResponse<ScimSchemaResponse>>(200, ScimRequestFilter.ContentType);
        Describe(group.MapGet("/Schemas/{id}", (string id, ScimOptions options) =>
            id == ScimSchemas.User ? Json(ScimDiscovery.UserSchema(options)) : id == ScimSchemas.Group ? Json(ScimDiscovery.GroupSchema(options)) : throw Missing()),
            "ScimSchema", "Read the supported SCIM User schema.")
            .Produces<ScimSchemaResponse>(200, ScimRequestFilter.ContentType);
        Describe(group.MapMethods("/{**path}", ["GET", "POST", "PUT", "PATCH", "DELETE"], IResult () => throw Missing()),
            "ScimUnsupportedResource", "Reject unsupported SCIM resources without bypassing machine authentication.");
    }

    private static RouteHandlerBuilder Describe(RouteHandlerBuilder route, string name, string summary) =>
        route.WithName(name).WithSummary(summary)
            .WithDescription("Requires the dedicated SCIM bearer token and HTTPS; interactive cookies and app roles do not authorize provisioning.")
            .Produces<ScimErrorResponse>(400, ScimRequestFilter.ContentType)
            .Produces<ScimErrorResponse>(401, ScimRequestFilter.ContentType)
            .Produces<ScimErrorResponse>(403, ScimRequestFilter.ContentType)
            .Produces<ScimErrorResponse>(404, ScimRequestFilter.ContentType)
            .Produces<ScimErrorResponse>(409, ScimRequestFilter.ContentType)
            .Produces<ScimErrorResponse>(412, ScimRequestFilter.ContentType)
            .Produces<ScimErrorResponse>(413, ScimRequestFilter.ContentType)
            .Produces<ScimErrorResponse>(415, ScimRequestFilter.ContentType)
            .Produces<ScimErrorResponse>(503, ScimRequestFilter.ContentType);

    private static Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<T> Json<T>(T value, int status = 200) =>
        TypedResults.Json(value, ScimRequestFilter.JsonOptions, ScimRequestFilter.ContentType, statusCode: status);
    private static Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<ScimUserResponse> User(
        HttpContext http, ScimUserResponse user, int status = 200)
    {
        http.Response.Headers.ETag = user.Meta.Version;
        return Json(user, status);
    }
    private static Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<ScimGroupResponse> Group(HttpContext http, ScimGroupResponse resource, int status = 200)
    {
        http.Response.Headers.ETag = resource.Meta.Version;
        return Json(resource, status);
    }
    private static ScimListResponse<T> List<T>(params T[] values) => new([ScimSchemas.List], values.Length, 1, values.Length, values);
    private static ScimProtocolException Missing() => new(404, "The SCIM resource was not found.");
    private static Guid Id(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id : throw Missing();
    private static int Number(HttpContext http, string name, int fallback) =>
        !http.Request.Query.TryGetValue(name, out var value) ? fallback :
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number :
        throw new ScimProtocolException(400, "Pagination parameters must be non-negative integers.", "invalidValue");
    private static string? Version(HttpContext http) =>
        http.Request.Headers.IfMatch.Count == 0 ? null : http.Request.Headers.IfMatch.ToString();

    private static async Task<JsonDocument> BodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type) ||
            !(string.Equals(type.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type.MediaType, "application/scim+json", StringComparison.OrdinalIgnoreCase)) ||
            type.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))
            throw new ScimProtocolException(415, "Use application/scim+json or application/json.");
        const int maximum = 64 * 1024;
        if (request.ContentLength > maximum) throw new ScimProtocolException(413, "The SCIM request exceeds 64 KiB.");
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maximum) throw new ScimProtocolException(413, "The SCIM request exceeds 64 KiB.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
    }
}
