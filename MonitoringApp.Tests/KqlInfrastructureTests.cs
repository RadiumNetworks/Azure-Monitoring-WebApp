using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Query.Logs;
using Azure.Monitor.Query.Logs.Models;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.DependencyInjection;
using System.ClientModel.Primitives;

namespace MonitoringApp.Tests;

public sealed class KqlInfrastructureTests
{
    [Fact]
    public void RegistrationPreservesOptionsAndAllSingletonsAcrossScopes()
    {
        var options = CrossTenantLogAnalyticsOptionsTests.CreateValid();
        var registrations = new ServiceCollection().AddKqlServices(options);
        Assert.Equal(9, registrations.Count);
        Assert.All(registrations, descriptor => Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime));
        Assert.Equal(9, registrations.Select(descriptor => descriptor.ServiceType).Distinct().Count());
        using var provider = registrations.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        foreach (var registration in registrations)
            Assert.Same(first.ServiceProvider.GetRequiredService(registration.ServiceType),
                second.ServiceProvider.GetRequiredService(registration.ServiceType));
        Assert.Same(options, provider.GetRequiredService<CrossTenantLogAnalyticsOptions>());
        Assert.IsType<KeyVaultLogAnalyticsSecretProvider>(provider.GetRequiredService<ILogAnalyticsSecretProvider>());
        Assert.IsType<AzureLogAnalyticsWorkspaceProbe>(provider.GetRequiredService<ILogAnalyticsWorkspaceProbe>());
        Assert.IsType<AzureAnalysisLogAnalyticsQuery>(provider.GetRequiredService<IAnalysisLogAnalyticsQuery>());
        Assert.Same(provider.GetRequiredService<TargetWorkspaceResolver>(),
            provider.GetRequiredService<AnalysisQueryRunner>().WorkspaceResolver);
    }

    [Fact]
    public async Task DisabledRegistrationResolvesWithoutCredentialOrVaultConfiguration()
    {
        using var provider = new ServiceCollection().AddKqlServices(new CrossTenantLogAnalyticsOptions())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var report = await provider.GetRequiredService<LogAnalyticsConnectionDiagnostics>().RunAsync();

        Assert.Equal(DiagnosticStatus.Disabled, report.KeyVault.Status);
        Assert.Empty(report.Workspaces);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetRequiredService<ILogAnalyticsSecretProvider>().GetClientSecretAsync(default));
        Assert.Equal("Cross-tenant Log Analytics is disabled.", error.Message);
    }

    [Fact]
    public void DefaultClientFactoryUsesTheExistingAzureCredentialAndClientTypes()
    {
        var options = CrossTenantLogAnalyticsOptionsTests.CreateValid();
        var clients = new LogAnalyticsClientFactory();

        var credential = clients.CreateCredential(options.Workspaces[0], options.ApplicationClientId, "test-secret");

        Assert.IsType<ClientSecretCredential>(credential);
        Assert.IsType<LogsQueryClient>(clients.CreateClient(credential));
    }

    [Fact]
    public async Task ConcurrentSecretReadersShareOneRefreshAndRotateAtExactlyFiveMinutes()
    {
        var options = CrossTenantLogAnalyticsOptionsTests.CreateValid();
        var clock = new FakeClock();
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeSecretClient { Read = _ => gate.Task };
        var secrets = new KeyVaultLogAnalyticsSecretProvider(options, client, clock);
        var readers = Enumerable.Range(0, 32).Select(_ => secrets.GetClientSecretAsync(default)).ToArray();
        Assert.Single(client.Requests);
        gate.SetResult("first-secret");
        Assert.All(await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(10)),
            value => Assert.Equal("first-secret", value));
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
        Assert.Equal("first-secret", await secrets.GetClientSecretAsync(default));
        Assert.Single(client.Requests);

        clock.Advance(TimeSpan.FromTicks(1));
        gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readers = Enumerable.Range(0, 32).Select(_ => secrets.GetClientSecretAsync(default)).ToArray();
        Assert.Equal(2, client.Requests.Count);
        gate.SetResult("rotated-secret");
        Assert.All(await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(10)),
            value => Assert.Equal("rotated-secret", value));
        Assert.Equal("rotated-secret", await secrets.GetClientSecretAsync(default));
        Assert.Equal(2, client.Requests.Count);
        Assert.All(client.Requests, request =>
        {
            Assert.Equal(options.ClientSecretName, request.Name);
            Assert.Null(request.Version);
            Assert.Null(request.ContentType);
        });
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("http")]
    [InlineData("cancelled")]
    public async Task FailedSecretRefreshDoesNotCacheFailureAndAlwaysReleasesTheLock(string failure)
    {
        var client = new FakeSecretClient
        {
            Read = _ => failure switch
            {
                "empty" => Task.FromResult(""),
                "http" => Task.FromException<string>(new RequestFailedException(403, "Denied")),
                "cancelled" => Task.FromException<string>(new TaskCanceledException()),
                _ => throw new ArgumentOutOfRangeException(nameof(failure))
            }
        };
        var secrets = new KeyVaultLogAnalyticsSecretProvider(
            CrossTenantLogAnalyticsOptionsTests.CreateValid(), client, new FakeClock());
        switch (failure)
        {
            case "empty":
                Assert.Equal("The configured Key Vault secret has an empty value.",
                    (await Assert.ThrowsAsync<InvalidOperationException>(() => secrets.GetClientSecretAsync(default))).Message);
                break;
            case "http":
                await Assert.ThrowsAsync<RequestFailedException>(() => secrets.GetClientSecretAsync(default));
                break;
            case "cancelled":
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secrets.GetClientSecretAsync(default));
                break;
        }

        client.Read = _ => Task.FromResult("recovered-secret");
        Assert.Equal("recovered-secret",
            await secrets.GetClientSecretAsync(default).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("recovered-secret", await secrets.GetClientSecretAsync(default));
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task CancellingAWaitingSecretReaderDoesNotCancelTheRefreshOrBlockOtherReaders()
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeSecretClient { Read = _ => gate.Task };
        var secrets = new KeyVaultLogAnalyticsSecretProvider(
            CrossTenantLogAnalyticsOptionsTests.CreateValid(), client, new FakeClock());
        using var owner = new CancellationTokenSource();
        var refresh = secrets.GetClientSecretAsync(owner.Token);
        using var waiting = new CancellationTokenSource();
        var cancelledReader = secrets.GetClientSecretAsync(waiting.Token);
        waiting.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledReader.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(refresh.IsCompleted);
        Assert.Equal(owner.Token, Assert.Single(client.Requests).Token);
        gate.SetResult("shared-secret");
        Assert.Equal("shared-secret", await refresh.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("shared-secret", await secrets.GetClientSecretAsync(default));
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task NormalAdapterForwardsTheExactRequestWithoutRunningTheDiagnosticProbe()
    {
        var options = CrossTenantLogAnalyticsOptionsTests.CreateValid();
        var clients = new FakeClientFactory();
        using var cancellation = new CancellationTokenSource();
        const string kql = "let x = 1;\nprint Value = x";

        var result = await new AzureAnalysisLogAnalyticsQuery(clients).QueryAsync(
            options.Workspaces[0], options.ApplicationClientId, "test-secret", kql, 43200, cancellation.Token);

        Assert.Same(clients.Client.Result, result);
        AssertCredentialRequest(clients, options);
        var request = Assert.Single(clients.Client.Requests);
        Assert.Equal(options.Workspaces[0].WorkspaceId, request.WorkspaceId);
        Assert.Equal(kql, request.Kql);
        Assert.Equal(TimeSpan.FromMinutes(43200), request.Range.Duration);
        Assert.Null(request.Options);
        Assert.Equal(cancellation.Token, request.Token);
        Assert.False(request.Typed);
        Assert.Empty(clients.Credential.Requests);
    }

    [Fact]
    public async Task ProbeUsesItsOwnQueryRangeScopeCorrelationIdAndOneSharedCancellationBudget()
    {
        var options = CrossTenantLogAnalyticsOptionsTests.CreateValid();
        var clients = new FakeClientFactory();
        using var cancellation = new CancellationTokenSource();

        var result = await new AzureLogAnalyticsWorkspaceProbe(clients).ProbeAsync(
            options.Workspaces[0], options.ApplicationClientId, "test-secret", TimeSpan.FromSeconds(37), cancellation.Token);

        AssertCredentialRequest(clients, options);
        var token = Assert.Single(clients.Credential.Requests);
        Assert.Equal(["https://api.loganalytics.io/.default"], token.Context.Scopes);
        var request = Assert.Single(clients.Client.Requests);
        Assert.True(request.Typed);
        Assert.Equal(options.Workspaces[0].WorkspaceId, request.WorkspaceId);
        Assert.Equal("print Status = \"ok\"", request.Kql);
        Assert.Equal(TimeSpan.FromMinutes(5), request.Range.Duration);
        Assert.Null(request.Options);
        Assert.True(request.Token.CanBeCanceled);
        Assert.NotEqual(cancellation.Token, request.Token);
        Assert.Equal(token.Token, request.Token);
        Assert.Equal(DiagnosticStatus.Succeeded, result.Token.Status);
        Assert.Equal(DiagnosticStatus.Succeeded, result.Query.Status);
        Assert.Equal(DiagnosticNameCode.TenantToken, result.Token.NameCode);
        Assert.Equal(DiagnosticNameCode.WorkspaceQuery, result.Query.NameCode);
        Assert.Equal(DiagnosticMessageCode.Succeeded, result.Token.MessageCode);
        Assert.Equal(DiagnosticMessageCode.Succeeded, result.Query.MessageCode);
        Assert.Equal("test-request-id", result.Query.CorrelationId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ProbeRejectsMissingDifferentOrMultipleSuccessRows(int resultKind)
    {
        var clients = new FakeClientFactory();
        clients.Client.Rows = resultKind switch
        {
            0 => [],
            1 => ["OK"],
            2 => ["ok", "ok"],
            _ => throw new ArgumentOutOfRangeException(nameof(resultKind))
        };

        var result = await ProbeAsync(clients);

        Assert.Equal(DiagnosticStatus.Succeeded, result.Token.Status);
        Assert.Equal(DiagnosticStatus.Failed, result.Query.Status);
        Assert.Equal(DiagnosticMessageCode.QueryUnexpected, result.Query.MessageCode);
        Assert.Equal("The workspace returned an unexpected test-query result.", result.Query.Message);
    }

    [Fact]
    public async Task TokenAuthenticationFailureSkipsQueryClientCreation()
    {
        var clients = new FakeClientFactory();
        clients.Credential.Acquire = _ => Task.FromException<AccessToken>(new AuthenticationFailedException("adapter detail"));

        var result = await ProbeAsync(clients);

        Assert.Equal(DiagnosticStatus.Failed, result.Token.Status);
        Assert.Equal(DiagnosticMessageCode.TokenAuthenticationFailed, result.Token.MessageCode);
        Assert.Equal("Microsoft Entra token acquisition failed.", result.Token.Message);
        Assert.Equal(DiagnosticStatus.NotRun, result.Query.Status);
        Assert.Equal(DiagnosticMessageCode.TokenRequired, result.Query.MessageCode);
        Assert.Equal(0, clients.ClientCreations);
        Assert.Empty(clients.Client.Requests);
    }

    [Fact]
    public async Task QueryHttpFailureRetainsSuccessfulTokenWithoutExposingAdapterDetails()
    {
        var clients = new FakeClientFactory();
        clients.Client.BeforeTypedQuery = _ => Task.FromException(
            new RequestFailedException(403, "adapter detail", "Forbidden", null));

        var result = await ProbeAsync(clients);

        Assert.Equal(DiagnosticStatus.Succeeded, result.Token.Status);
        Assert.Equal(DiagnosticStatus.Failed, result.Query.Status);
        Assert.Equal("Azure request failed with HTTP 403 (Forbidden).", result.Query.Message);
        Assert.Equal(DiagnosticMessageCode.QueryRequestFailed, result.Query.MessageCode);
        Assert.Equal(403, result.Query.HttpStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProbeDeadlineCancelsTheActiveStageAndReportsTheCorrectTimeout(bool duringQuery)
    {
        var clients = new FakeClientFactory();
        if (duringQuery)
            clients.Client.BeforeTypedQuery = token => Task.Delay(Timeout.Infinite, token);
        else
            clients.Credential.Acquire = async token =>
            {
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("The delay must be cancelled.");
            };

        var result = await ProbeAsync(clients, timeout: TimeSpan.FromMilliseconds(50)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(Assert.Single(clients.Credential.Requests).Token.IsCancellationRequested);
        Assert.Equal(duringQuery ? DiagnosticStatus.Succeeded : DiagnosticStatus.Failed, result.Token.Status);
        Assert.Equal(duringQuery ? DiagnosticStatus.Failed : DiagnosticStatus.NotRun, result.Query.Status);
        Assert.Equal(duringQuery ? "Workspace query timed out." : "Token acquisition timed out.",
            duringQuery ? result.Query.Message : result.Token.Message);
        Assert.Equal(duringQuery ? DiagnosticMessageCode.QueryTimeout : DiagnosticMessageCode.TokenTimeout,
            duringQuery ? result.Query.MessageCode : result.Token.MessageCode);
        Assert.Equal(duringQuery ? 1 : 0, clients.ClientCreations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProbePropagatesCallerCancellationFromEitherStage(bool duringQuery)
    {
        var clients = new FakeClientFactory();
        using var cancellation = new CancellationTokenSource();
        if (duringQuery)
            clients.Client.BeforeTypedQuery = token =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(token);
            };
        else
            clients.Credential.Acquire = token =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<AccessToken>(token);
            };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProbeAsync(clients, cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(duringQuery ? 1 : 0, clients.ClientCreations);
    }

    private static Task<LogAnalyticsWorkspaceProbeResult> ProbeAsync(
        FakeClientFactory clients, CancellationToken token = default, TimeSpan? timeout = null)
    {
        var options = CrossTenantLogAnalyticsOptionsTests.CreateValid();
        return new AzureLogAnalyticsWorkspaceProbe(clients).ProbeAsync(
            options.Workspaces[0], options.ApplicationClientId, "test-secret", timeout ?? TimeSpan.FromSeconds(30), token);
    }

    private static void AssertCredentialRequest(FakeClientFactory clients, CrossTenantLogAnalyticsOptions options)
    {
        var request = Assert.Single(clients.Requests);
        Assert.Same(options.Workspaces[0], request.Workspace);
        Assert.Equal(options.ApplicationClientId, request.ApplicationClientId);
        Assert.Equal("test-secret", request.Secret);
        Assert.Equal(1, clients.ClientCreations);
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class FakeSecretClient : SecretClient
    {
        public Func<CancellationToken, Task<string>> Read { get; set; } = _ => Task.FromResult("test-secret");
        public List<(string Name, string? Version, SecretContentType? ContentType, CancellationToken Token)> Requests { get; } = [];
        public override async Task<Response<KeyVaultSecret>> GetSecretAsync(
            string name, string? version = null, SecretContentType? outContentType = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add((name, version, outContentType, cancellationToken));
            return Response.FromValue(new KeyVaultSecret(name, await Read(cancellationToken)), new TestResponse());
        }
    }

    [Fact]
    public async Task FreeQueryClientSendsExactKqlWithAllTimeRange()
    {
        var factory = new FakeClientFactory();
        var client = new AzureFreeKqlQueryClient(factory);
        var workspace = new CrossTenantLogAnalyticsWorkspaceOptions { Key = "free", WorkspaceId = "workspace" };
        const string kql = "  Heartbeat\n| where TimeGenerated > ago(90d)\n| project Computer, Status__Status  ";
        using var cancellation = new CancellationTokenSource();
        await client.QueryAsync(workspace, "app", "test-secret", kql, cancellation.Token);
        var request = Assert.Single(factory.Client.Requests);
        Assert.Equal(kql, request.Kql);
        Assert.Equal(LogsQueryTimeRange.All, request.Range);
        Assert.Equal(cancellation.Token, request.Token);
        Assert.Equal("workspace", request.WorkspaceId);
    }

    private sealed class FakeClientFactory : LogAnalyticsClientFactory
    {
        public FakeCredential Credential { get; } = new();
        public FakeQueryClient Client { get; } = new();
        public int ClientCreations { get; private set; }
        public List<(CrossTenantLogAnalyticsWorkspaceOptions Workspace, string ApplicationClientId, string Secret)> Requests { get; } = [];
        internal override TokenCredential CreateCredential(
            CrossTenantLogAnalyticsWorkspaceOptions workspace, string applicationClientId, string secret)
        {
            Requests.Add((workspace, applicationClientId, secret));
            return Credential;
        }
        internal override LogsQueryClient CreateClient(TokenCredential credential)
        {
            Assert.Same(Credential, credential);
            ClientCreations++;
            return Client;
        }
    }

    private sealed class FakeCredential : TokenCredential
    {
        public Func<CancellationToken, Task<AccessToken>> Acquire { get; set; } =
            _ => Task.FromResult(new AccessToken("test-token", DateTimeOffset.UtcNow.AddMinutes(10)));
        public List<(TokenRequestContext Context, CancellationToken Token)> Requests { get; } = [];
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Only asynchronous token acquisition is expected.");
        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Requests.Add((requestContext, cancellationToken));
            return await Acquire(cancellationToken);
        }
    }

    private sealed class FakeQueryClient : LogsQueryClient
    {
        public LogsQueryResult Result { get; } = ModelReaderWriter.Read<LogsQueryResult>(
            BinaryData.FromString("""{"tables":[{"name":"PrimaryResult","columns":[{"name":"Value","type":"long"}],"rows":[[1]]}]}"""))!;
        public string[] Rows { get; set; } = ["ok"];
        public Func<CancellationToken, Task> BeforeTypedQuery { get; set; } = _ => Task.CompletedTask;
        public List<(string WorkspaceId, string Kql, LogsQueryTimeRange Range, LogsQueryOptions? Options,
            CancellationToken Token, bool Typed)> Requests { get; } = [];
        public override Task<Response<LogsQueryResult>> QueryWorkspaceAsync(
            string workspaceId, string query, LogsQueryTimeRange timeRange,
            LogsQueryOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests.Add((workspaceId, query, timeRange, options, cancellationToken, false));
            return Task.FromResult(Response.FromValue(Result, new TestResponse()));
        }
        public override async Task<Response<IReadOnlyList<T>>> QueryWorkspaceAsync<T>(
            string workspaceId, string query, LogsQueryTimeRange timeRange,
            LogsQueryOptions? options = null, CancellationToken cancellationToken = default)
        {
            Assert.Equal(typeof(string), typeof(T));
            Requests.Add((workspaceId, query, timeRange, options, cancellationToken, true));
            await BeforeTypedQuery(cancellationToken);
            return Response.FromValue<IReadOnlyList<T>>(Rows.Cast<T>().ToArray(), new TestResponse());
        }
    }

    private sealed class TestResponse : Response
    {
        public override int Status => 200;
        public override string ReasonPhrase => "OK";
        public override Stream? ContentStream { get; set; }
        public override string ClientRequestId { get; set; } = "";
        public override void Dispose() { }
        protected override bool ContainsHeader(string name) => name.Equals("x-ms-request-id", StringComparison.OrdinalIgnoreCase);
        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [new("x-ms-request-id", "test-request-id")];
        protected override bool TryGetHeader(string name, out string value)
        {
            value = ContainsHeader(name) ? "test-request-id" : "";
            return ContainsHeader(name);
        }
        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values)
        {
            values = ContainsHeader(name) ? ["test-request-id"] : [];
            return ContainsHeader(name);
        }
    }
}
