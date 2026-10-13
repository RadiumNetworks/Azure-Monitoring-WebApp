namespace MonitoringApp.Tests;

using System.Net;

public sealed class MicrosoftSourceCatalogTests
{
    [Fact]
    public void DefaultCatalogContainsVersionedValidatedInitialSources()
    {
        var catalog = MicrosoftSourceCatalog.CreateDefault();

        Assert.Equal(MicrosoftSourceCatalog.CurrentVersion, catalog.Version);
        Assert.Equal(MicrosoftSourceCatalog.CuratedOnlyMode, catalog.SearchMode);
        Assert.False(catalog.RuntimeSearchEnabled);
        Assert.Equal(7, catalog.Sources.Count);
        Assert.Empty(catalog.Validate());
        Assert.All(catalog.Sources, source =>
        {
            Assert.Equal(Uri.UriSchemeHttps, source.Url.Scheme);
            Assert.Contains(source.Url.IdnHost, catalog.AllowedHosts);
            Assert.NotEmpty(source.Topics);
            Assert.NotEmpty(source.SymptomsOrErrorCodes);
        });
    }

    [Theory]
    [InlineData("http://learn.microsoft.com/example", "Windows Server Active Directory Domain Services")]
    [InlineData("https://example.com/docs", "Windows Server Active Directory Domain Services")]
    [InlineData("https://learn.microsoft.com/example", "Unapproved product")]
    public void RejectsUnapprovedSource(string url, string product)
    {
        var source = CreateSource("source-a", url, product);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new MicrosoftSourceCatalog("test-v1", [source]));

        Assert.Contains("Invalid Microsoft source catalog", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsDuplicateIdsAndRuntimeSearchWithoutAuditImplementation()
    {
        var source = CreateSource(
            "duplicate",
            "https://learn.microsoft.com/windows-server/identity/ad-ds/manage/ad-ds-troubleshooting",
            "Windows Server Active Directory Domain Services");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new MicrosoftSourceCatalog("test-v1", [source, source], runtimeSearchEnabled: true));

        Assert.Contains("duplicated", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Runtime Microsoft search is not approved", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BlocksRedirectToUnapprovedHost()
    {
        var catalog = CreateSingleSourceCatalog();
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://example.com/untrusted") }
        }));
        var checker = new MicrosoftSourceLinkChecker(client, catalog);

        var result = await checker.VerifyAsync(catalog.Sources[0]);

        Assert.False(result.Reachable);
        Assert.True(catalog.IsBlocked(result.SourceId));
        Assert.Empty(catalog.ApprovedSources);
        Assert.Contains("not an approved", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptsApprovedRedirectAndUnblocksSource()
    {
        var catalog = CreateSingleSourceCatalog();
        var requests = 0;
        using var client = new HttpClient(new StubHandler(_ =>
        {
            requests++;
            return requests == 1
                ? new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri("/canonical", UriKind.Relative) }
                }
                : new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var checker = new MicrosoftSourceLinkChecker(client, catalog);

        var result = await checker.VerifyAsync(catalog.Sources[0]);

        Assert.True(result.Reachable);
        Assert.Equal("https://learn.microsoft.com/canonical", result.FinalUrl?.AbsoluteUri);
        Assert.False(catalog.IsBlocked(result.SourceId));
        Assert.Single(catalog.ApprovedSources);
    }

    private static MicrosoftSourceCatalog CreateSingleSourceCatalog() => new(
        "test-v1",
        [CreateSource(
            "source-a",
            "https://learn.microsoft.com/start",
            "Windows Server Active Directory Domain Services")]);

    private static MicrosoftSource CreateSource(string id, string url, string product) => new(
        id,
        "Test source",
        new Uri(url, UriKind.Absolute),
        product,
        ["test topic"],
        ["test symptom"],
        "Tested",
        new DateOnly(2026, 9, 16));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}