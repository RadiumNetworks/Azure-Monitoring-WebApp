using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace MonitoringApp.Tests;

public sealed class ScimProtocolTests
{
    private static readonly ProvisioningProfile Original = new(Guid.NewGuid(), "old@contoso.test",
        "Original", "First", "Last", "mail@contoso.test", true);

    internal static JsonElement User(Guid? externalId = null, string userName = "user@contoso.test") =>
        JsonSerializer.SerializeToElement(new
        {
            schemas = new[] { ScimSchemas.User }, externalId = (externalId ?? Guid.NewGuid()).ToString("D"),
            userName, displayName = "Source display", name = new { givenName = "First", familyName = "Last" },
            emails = new[] { new { value = "work@contoso.test", type = "work", primary = true } }, active = true
        });

    internal static JsonElement Patch(string operations)
    {
        using var document = JsonDocument.Parse(operations);
        return JsonSerializer.SerializeToElement(new { schemas = new[] { ScimSchemas.Patch }, Operations = document.RootElement });
    }

    [Fact]
    public void DisabledScimNeedsNoConfigurationAndEnabledScimRequiresDirectoryAndValidatedSecrets()
    {
        Assert.Empty(new ScimOptions().Validate());
        var options = new ScimOptions { Enabled = true };
        Assert.Equal(4, options.Validate().Count);
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddScimServices(options, new()));
    }

    [Theory]
    [InlineData("http://example.test/scim/v2")]
    [InlineData("https://user@example.test/scim/v2")]
    [InlineData("https://example.test/scim/v2?tenant=other")]
    [InlineData("https://example.test/scim/v2#fragment")]
    [InlineData("https://example.test/not-scim")]
    public void EnabledConfigurationRejectsUnsafeOrAmbiguousPublicEndpoints(string url)
    {
        var options = new ScimOptions
        {
            Enabled = true, ConnectionId = Guid.NewGuid(), TenantId = Guid.NewGuid(),
            BaseUrl = url, TokenHashes = [new string('A', 64)]
        };
        Assert.Contains(options.Validate(), error => error.StartsWith("Scim.BaseUrl", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("userName eq \"a@contoso.test\"", "username", "a@contoso.test")]
    [InlineData("  USERNAME EQ \"a\\\"b\" ", "username", "a\"b")]
    [InlineData("externalId eq \"01000000-0000-0000-0000-000000000001\"", "externalid", "01000000-0000-0000-0000-000000000001")]
    public void MatchingFiltersAreExplicitAndDecodeJsonStrings(string input, string attribute, string value) =>
        Assert.Equal(new ProvisioningFilter(attribute, value), ScimUserProtocol.Filter(input));

    [Theory]
    [InlineData("active eq true")]
    [InlineData("userName co \"a\"")]
    [InlineData("userName eq \"a\" or userName pr")]
    [InlineData("externalId eq \"not-an-object-id\"")]
    [InlineData("externalId eq \"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("userName eq \"\\z\"")]
    public void UnsupportedOrMalformedFiltersNeverBecomeUnfilteredLists(string input)
    {
        var error = Assert.Throws<ScimProtocolException>(() => ScimUserProtocol.Filter(input));
        Assert.Equal((400, "invalidFilter"), (error.Status, error.ScimType));
    }

    [Fact]
    public void ReadMapsAllSourceAttributesWithoutInventingEmailOrNames()
    {
        var json = User();
        var profile = ScimUserProtocol.Read(json);
        Assert.Equal(("user@contoso.test", "Source display", "First", "Last", "work@contoso.test", true),
            (profile.UserName, profile.DisplayName, profile.GivenName, profile.FamilyName, profile.Email, profile.Active));
        var minimal = JsonSerializer.SerializeToElement(new { schemas = new[] { ScimSchemas.User }, externalId = Guid.NewGuid(), userName = "upn@contoso.test" });
        var result = ScimUserProtocol.Read(minimal);
        Assert.Null(result.GivenName);
        Assert.Null(result.FamilyName);
        Assert.Null(result.Email);
        Assert.True(result.Active);
    }

    [Theory]
    [InlineData("""[{"op":"replace","path":"name.givenName","value":"New"}]""", "New", "Last")]
    [InlineData("""[{"op":"add","path":"name","value":{"givenName":"New"}}]""", "New", "Last")]
    [InlineData("""[{"op":"replace","path":"name","value":{"givenName":"New"}}]""", "New", null)]
    [InlineData("""[{"op":"replace","path":"name","value":null}]""", null, null)]
    [InlineData("""[{"op":"remove","path":"name"}]""", null, null)]
    [InlineData("""[{"OP":"Replace","PATH":"NAME.GIVENNAME","VALUE":"New"}]""", "New", "Last")]
    public void NamePatchDistinguishesMergeReplacementRemovalAndCase(string operations, string? given, string? family)
    {
        var result = ScimUserProtocol.Patch(Original, Patch(operations));
        Assert.Equal(given, result.GivenName);
        Assert.Equal(family, result.FamilyName);
        Assert.Equal(Original.Email, result.Email);
        Assert.Equal(Original.ExternalId, result.ExternalId);
    }

    [Fact]
    public void AtomicPatchSupportsNoPathAndWorkEmailSelectors()
    {
        var result = ScimUserProtocol.Patch(Original, Patch("""
            [{"op":"replace","value":{"userName":"renamed@contoso.test","active":false,"displayName":"Changed"}},
             {"op":"replace","path":"emails[type eq \"work\"].value","value":"new@contoso.test"},
             {"op":"remove","path":"name.familyName"}]
            """));
        Assert.Equal(("renamed@contoso.test", false, "Changed", "new@contoso.test", null),
            (result.UserName, result.Active, result.DisplayName, result.Email, result.FamilyName));
        var removed = ScimUserProtocol.Patch(result, Patch("""[{"op":"remove","path":"emails[type eq \"work\"]"}]"""));
        Assert.Null(removed.Email);
        Assert.Throws<ScimProtocolException>(() =>
            ScimUserProtocol.Patch(removed, Patch("""[{"op":"remove","path":"emails[type eq \"work\"]"}]""")));
    }

    [Theory]
    [InlineData("""[{"op":"replace","path":"roles","value":["Admin"]}]""", "invalidPath")]
    [InlineData("""[{"op":"remove","path":"userName"}]""", "mutability")]
    [InlineData("""[{"op":"replace","path":"externalId","value":"02000000-0000-0000-0000-000000000002"}]""", "mutability")]
    [InlineData("""[{"op":"replace","path":"active","value":"False"}]""", "invalidValue")]
    [InlineData("""[{"op":"remove"}]""", "noTarget")]
    [InlineData("""[{"op":"replace","path":"active"}]""", "invalidValue")]
    [InlineData("""[{"op":"move","path":"active","value":true}]""", "invalidSyntax")]
    [InlineData("""[{"op":"replace","path":"emails","value":[{"type":"home","value":"home@contoso.test"}]}]""", "invalidValue")]
    public void InvalidPatchNeverMutatesTheOriginalProfile(string operations, string code)
    {
        var error = Assert.Throws<ScimProtocolException>(() => ScimUserProtocol.Patch(Original, Patch(operations)));
        Assert.Equal(code, error.ScimType);
        Assert.Equal("old@contoso.test", Original.UserName);
        Assert.True(Original.Active);
    }

    [Theory]
    [InlineData("""{"schemas":[],"externalId":"01000000-0000-0000-0000-000000000001","userName":"u"}""")]
    [InlineData("""{"schemas":["urn:ietf:params:scim:schemas:core:2.0:User"],"externalId":"bad","userName":"u"}""")]
    [InlineData("""{"schemas":["urn:ietf:params:scim:schemas:core:2.0:User"],"externalId":"01000000-0000-0000-0000-000000000001","userName":"u","UserName":"v"}""")]
    [InlineData("""{"schemas":["urn:ietf:params:scim:schemas:core:2.0:User"],"externalId":"01000000-0000-0000-0000-000000000001","userName":"u","roles":["Admin"]}""")]
    [InlineData("""{"schemas":["urn:ietf:params:scim:schemas:core:2.0:User"],"externalId":"01000000-0000-0000-0000-000000000001","userName":"u","name":{"givenName":"a","GivenName":"b"}}""")]
    public void InvalidProfilesAreRejectedExplicitly(string body)
    {
        using var document = JsonDocument.Parse(body);
        Assert.Equal(400, Assert.Throws<ScimProtocolException>(() => ScimUserProtocol.Read(document.RootElement)).Status);
    }
}
