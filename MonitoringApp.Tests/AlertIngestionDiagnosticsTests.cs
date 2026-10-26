using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MonitoringApp.Components.Pages;
using MonitoringApp.Middleware;

namespace MonitoringApp.Tests;

public sealed class AlertIngestionDiagnosticsTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Client = "22222222-2222-2222-2222-222222222222";
    private const string Audience = "33333333-3333-3333-3333-333333333333";
    private const string ObjectId = "44444444-4444-4444-4444-444444444444";

    [Fact]
    public void DisabledAuthenticationReportsClaimsWithoutAuthenticatingThem()
    {
        var options = Options(false);
        var diagnostics = new AlertIngestionDiagnostics(options, new ManualTimeProvider());
        diagnostics.Record(Context(Token(Payload())), null);

        var record = Assert.Single(diagnostics.GetRecent());
        Assert.False(record.AuthenticationRequired);
        Assert.False(record.TokenValidated);
        Assert.Contains("disabled", record.AuthenticationStatus);
        Assert.Equal(Tenant, record.TenantId);
        Assert.Equal(Client, record.UamiClientId);
        Assert.Equal(ObjectId, record.ObjectId);
        Assert.Equal("2.0", record.TokenVersion);
        Assert.Equal([Audience], record.Audiences);
        Assert.Equal(["Alerts.Write"], record.Roles);
        Assert.True(record.RequiredRolePresent);
        Assert.All(record.PolicyChecks, check => Assert.True(check.Passed, check.Name));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(2000000000), record.ExpiresAt);
    }

    [Fact]
    public void ValidatedIdentityTakesPrecedenceOverUnverifiedHeaderClaims()
    {
        var diagnostics = new AlertIngestionDiagnostics(Options(true), new ManualTimeProvider());
        var payload = Payload();
        payload["tid"] = "spoofed-tenant";
        var result = AuthenticateResult.Success(new AuthenticationTicket(Principal(), "Bearer"));
        diagnostics.Record(Context(Token(payload)), result);

        var record = Assert.Single(diagnostics.GetRecent());
        Assert.True(record.TokenValidated);
        Assert.True(record.AuthenticationRequired);
        Assert.Equal(Tenant, record.TenantId);
        Assert.Contains("validated Bearer identity", record.InspectionStatus);
    }

    [Theory]
    [InlineData("Bearer not-a-jwt")]
    [InlineData("Basic c2VjcmV0")]
    [InlineData("Bearer")]
    [InlineData("Bearer eyJhbGciOiJub25lIn0.e30")]
    [InlineData("Bearer eyJhbGciOiJub25lIn0.bm90LWpzb24.")]
    [InlineData("")]
    public void UnreadableHeadersAreExplicitAndDoNotCreateClaimChecks(string header)
    {
        var context = Context();
        context.Request.Headers.Authorization = header;
        var diagnostics = new AlertIngestionDiagnostics(Options(false), new ManualTimeProvider());
        diagnostics.Record(context, null);

        var record = Assert.Single(diagnostics.GetRecent());
        Assert.False(record.TokenValidated);
        Assert.Null(record.TenantId);
        Assert.Null(record.RequiredRolePresent);
        Assert.Empty(record.PolicyChecks);
        Assert.False(string.IsNullOrWhiteSpace(record.InspectionStatus));
    }

    [Fact]
    public void InspectionLimitsDoNotRejectTheRequestOrRetainTheHeader()
    {
        var context = Context();
        context.Request.Headers.Authorization = "Bearer " + new string('x', 16 * 1024);
        context.Response.StatusCode = 201;
        var diagnostics = new AlertIngestionDiagnostics(Options(false), new ManualTimeProvider());
        diagnostics.Record(context, null);

        var record = Assert.Single(diagnostics.GetRecent());
        Assert.Equal(201, context.Response.StatusCode);
        Assert.Equal(201, record.StatusCode);
        Assert.Contains("inspection limit", record.InspectionStatus);
        Assert.Null(record.UamiClientId);
    }

    [Fact]
    public void V1ClientIdIsDisplayedButDoesNotBypassExistingV2Policy()
    {
        var payload = Payload();
        payload.Remove("azp");
        payload["appid"] = Client;
        payload["ver"] = "1.0";
        payload["iss"] = $"https://sts.windows.net/{Tenant}/";
        var diagnostics = new AlertIngestionDiagnostics(Options(false), new ManualTimeProvider());
        diagnostics.Record(Context(Token(payload)), null);

        var record = Assert.Single(diagnostics.GetRecent());
        Assert.Equal(Client, record.UamiClientId);
        Assert.Equal(Client, record.ApplicationId);
        Assert.Null(record.AuthorizedParty);
        Assert.True(record.RequiredRolePresent);
        Assert.Contains(record.PolicyChecks, check => !check.Passed && check.Name.Contains("issuer"));
        Assert.Contains(record.PolicyChecks, check => !check.Passed && check.Name.Contains("pair"));
    }

    [Fact]
    public void WrongIdentityAndMissingRoleAreReportedSeparately()
    {
        var payload = Payload();
        payload["azp"] = ObjectId;
        payload.Remove("roles");
        var diagnostics = new AlertIngestionDiagnostics(Options(false), new ManualTimeProvider());
        diagnostics.Record(Context(Token(payload)), null);

        var record = Assert.Single(diagnostics.GetRecent());
        Assert.False(record.RequiredRolePresent);
        Assert.Contains(record.PolicyChecks, check => !check.Passed && check.Name.Contains("pair"));
    }

    [Fact]
    public void DoesNotRetainTokenBodyQueryCookiesOrExceptionMessages()
    {
        var payload = Payload();
        payload["unselected"] = "private-claim-sentinel";
        var token = Token(payload);
        var context = Context(token);
        context.Request.QueryString = new QueryString("?secret=query-sentinel");
        context.Request.Headers.Cookie = "session=cookie-sentinel";
        context.Request.Body = new MemoryStream("body-sentinel"u8.ToArray());
        var diagnostics = new AlertIngestionDiagnostics(Options(true), new ManualTimeProvider());
        diagnostics.Record(context, AuthenticateResult.Fail(
            new SecurityTokenInvalidAudienceException("exception-sentinel " + token)));

        var record = Assert.Single(diagnostics.GetRecent());
        var json = JsonSerializer.Serialize(record);
        foreach (var secret in new[] { token, "private-claim-sentinel", "query-sentinel", "cookie-sentinel", "body-sentinel", "exception-sentinel" })
            Assert.DoesNotContain(secret, json);
        Assert.Equal(0, context.Request.Body.Position);
        Assert.Equal("/api/alerts", record.Endpoint);
        Assert.Equal("Rejected: invalid audience.", record.AuthenticationStatus);
        Assert.False(record.TokenValidated);
    }

    [Theory]
    [InlineData(typeof(SecurityTokenExpiredException), "expired")]
    [InlineData(typeof(SecurityTokenNotYetValidException), "not yet valid")]
    [InlineData(typeof(SecurityTokenInvalidIssuerException), "invalid issuer")]
    [InlineData(typeof(SecurityTokenInvalidSignatureException), "invalid signature")]
    [InlineData(typeof(SecurityTokenSignatureKeyNotFoundException), "signing key")]
    public void ValidationFailuresAreCategorizedWithoutOriginalMessages(Type exceptionType, string expected)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, "private-error-sentinel")!;
        var diagnostics = new AlertIngestionDiagnostics(Options(true), new ManualTimeProvider());
        diagnostics.Record(Context(), AuthenticateResult.Fail(exception));
        var record = Assert.Single(diagnostics.GetRecent());
        Assert.Contains(expected, record.AuthenticationStatus);
        Assert.DoesNotContain("private-error-sentinel", JsonSerializer.Serialize(record));
    }

    [Fact]
    public void BoundedListsAndValuesRemainWithinDocumentedLimits()
    {
        var payload = Payload();
        payload["iss"] = new string('i', 400);
        payload["roles"] = Enumerable.Range(0, 40)
            .Select(index => index == 0 ? new string('r', 300) : $"role-{index}").ToArray();
        payload["aud"] = Enumerable.Range(0, 10).Select(index => "audience-" + index).ToArray();
        payload["exp"] = long.MaxValue;
        var diagnostics = new AlertIngestionDiagnostics(Options(false), new ManualTimeProvider());
        diagnostics.Record(Context(Token(payload)), null);

        var record = Assert.Single(diagnostics.GetRecent());
        Assert.Equal(32, record.Roles.Count);
        Assert.Equal(8, record.Audiences.Count);
        Assert.Equal(256, record.Roles[0].Length);
        Assert.All(record.Roles, role => Assert.InRange(role.Length, 1, 256));
        Assert.Equal(256, record.Issuer!.Length);
        Assert.True(record.ListsTruncated);
        Assert.Null(record.ExpiresAt);
    }

    [Fact]
    public void KeepsExactlyTheNewestTwentyRequests()
    {
        var diagnostics = new AlertIngestionDiagnostics(Options(false), new ManualTimeProvider());
        for (var index = 0; index < 25; index++)
        {
            var context = Context();
            context.TraceIdentifier = $"trace-{index}";
            diagnostics.Record(context, null);
        }

        var records = diagnostics.GetRecent();
        Assert.Equal(20, records.Count);
        Assert.Equal("trace-24", records[0].TraceId);
        Assert.Equal("trace-5", records[^1].TraceId);
    }

    [Fact]
    public void ExpiresAtExactlyTwentyFourHoursAndNewInstancesStartEmpty()
    {
        var clock = new ManualTimeProvider();
        var diagnostics = new AlertIngestionDiagnostics(Options(false), clock);
        diagnostics.Record(Context(), null);
        clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromTicks(1));
        Assert.Single(diagnostics.GetRecent());
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Empty(diagnostics.GetRecent());
        diagnostics.Record(Context(), null);
        Assert.Single(diagnostics.GetRecent());
        Assert.Empty(new AlertIngestionDiagnostics(Options(false), clock).GetRecent());
    }

    [Fact]
    public void ConcurrentCaptureStaysWithinCapacity()
    {
        var diagnostics = new AlertIngestionDiagnostics(Options(false), new ManualTimeProvider());
        Parallel.For(0, 500, index =>
        {
            var context = Context();
            context.TraceIdentifier = $"parallel-{index}";
            diagnostics.Record(context, null);
        });
        var records = diagnostics.GetRecent();
        Assert.Equal(20, records.Count);
        Assert.Equal(20, records.Select(record => record.TraceId).Distinct().Count());
    }

    [Theory]
    [InlineData(true, 401)]
    [InlineData(true, 403)]
    [InlineData(false, 400)]
    [InlineData(false, 201)]
    public async Task MiddlewareRecordsCompletedResponsesWithoutChangingThem(bool enabled, int status)
    {
        var options = Options(enabled);
        var diagnostics = new AlertIngestionDiagnostics(options, new ManualTimeProvider());
        var feature = new CompletingResponseFeature();
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(feature);
        context.Request.Method = "POST";
        context.Request.Path = "/api/alerts";
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new AuthorizeAttribute(AlertIngestionAuthorization.PolicyName)), "Ingest"));
        var authentication = new StubAuthenticationService();
        await using var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(authentication).BuildServiceProvider();
        context.RequestServices = services;
        var middleware = new AlertIngestionDiagnosticsMiddleware(httpContext =>
        {
            httpContext.Response.StatusCode = status;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, diagnostics, options);
        Assert.Empty(diagnostics.GetRecent());
        await feature.CompleteAsync();

        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal(status, Assert.Single(diagnostics.GetRecent()).StatusCode);
        Assert.Equal(enabled ? 1 : 0, authentication.Calls);
    }

    [Fact]
    public async Task MiddlewareDoesNotCaptureUnrelatedEndpoints()
    {
        var diagnostics = new AlertIngestionDiagnostics(Options(false), new ManualTimeProvider());
        var feature = new CompletingResponseFeature();
        var context = Context();
        context.Features.Set<IHttpResponseFeature>(feature);
        context.Request.Path = "/api/query";
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new AuthorizeAttribute("AdminAccess")), "Other endpoint"));
        await new AlertIngestionDiagnosticsMiddleware(_ => Task.CompletedTask)
            .InvokeAsync(context, diagnostics, Options(false));
        await feature.CompleteAsync();
        Assert.Empty(diagnostics.GetRecent());
    }

    [Theory]
    [InlineData(false, false, false, 201, false)]
    [InlineData(false, true, true, 201, false)]
    [InlineData(true, false, false, 401, false)]
    [InlineData(true, true, false, 403, true)]
    [InlineData(true, true, true, 201, true)]
    public async Task RealHttpPipelinePreservesAuthenticationAndCapturesItsFinalResult(
        bool enabled, bool sendToken, bool hasRole, int expectedStatus, bool expectedValidated)
    {
        var options = Options(enabled);
        var diagnostics = new SignalingDiagnostics(new AlertIngestionDiagnostics(options, TimeProvider.System));
        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            Args = []
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IAlertIngestionDiagnostics>(diagnostics);
        builder.Services.AddAuthentication("TestCookie").AddCookie("TestCookie")
            .AddJwtBearer(bearer =>
            {
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = $"https://login.microsoftonline.com/{Tenant}/v2.0",
                    ValidAudience = Audience,
                    IssuerSigningKey = key
                };
            });
        builder.Services.AddAuthorizationBuilder().AddPolicy(AlertIngestionAuthorization.PolicyName, policy =>
        {
            if (enabled)
            {
                policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context => AlertIngestionAuthorization.IsAuthorized(context.User, options));
            }
            else
            {
                policy.RequireAssertion(_ => true);
            }
        });
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseMiddleware<AlertIngestionDiagnosticsMiddleware>();
        app.UseAuthorization();
        app.MapPost("/api/alerts", () => Results.StatusCode(201))
            .RequireAuthorization(AlertIngestionAuthorization.PolicyName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await app.StartAsync(timeout.Token);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)) };
            if (sendToken)
            {
                var claims = Principal().Claims.Where(claim => hasRole || claim.Type != "roles");
                var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
                {
                    Subject = new ClaimsIdentity(claims),
                    Expires = DateTime.UtcNow.AddMinutes(5),
                    SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
                });
                client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            }
            using var response = await client.PostAsync("/api/alerts", null, timeout.Token);
            var record = await diagnostics.Completed.Task.WaitAsync(timeout.Token);
            Assert.Equal(expectedStatus, (int)response.StatusCode);
            Assert.Equal(expectedStatus, record.StatusCode);
            Assert.Equal(expectedValidated, record.TokenValidated);
            Assert.Equal(enabled, record.AuthenticationRequired);
            Assert.Equal(enabled && expectedStatus is 401 or 403, record.AuthenticationBlocked);
            if (sendToken)
                Assert.Equal(hasRole, record.RequiredRolePresent);
        }
        finally
        {
            await app.StopAsync(timeout.Token);
        }
    }

    [Fact]
    public void DisabledStartupValidationIsUnchangedButDiagnosticsCanShowIncompleteConfiguration()
    {
        var options = new AlertIngestionAuthenticationOptions();
        Assert.Empty(options.Validate());
        Assert.Equal(2, options.ValidateConfiguration().Count);
    }

    [Fact]
    public void AdminPageUsesAdminAccessAndDedicatedRoute()
    {
        Assert.Equal("AdminAccess", Assert.Single(typeof(AdminAlertAuthentication)
            .GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>()).Policy);
        Assert.Equal("/admin/alert-authentication", Assert.Single(typeof(AdminAlertAuthentication)
            .GetCustomAttributes(typeof(RouteAttribute), true).Cast<RouteAttribute>()).Template);
    }

    private static AlertIngestionAuthenticationOptions Options(bool enabled) => new()
    {
        Enabled = enabled,
        Audience = Audience,
        RequiredRole = "Alerts.Write",
        Sources = [new() { TenantId = Tenant, ClientId = Client }]
    };

    private static Dictionary<string, object> Payload() => new()
    {
        ["tid"] = Tenant,
        ["azp"] = Client,
        ["oid"] = ObjectId,
        ["ver"] = "2.0",
        ["iss"] = $"https://login.microsoftonline.com/{Tenant}/v2.0",
        ["idtyp"] = "app",
        ["aud"] = Audience,
        ["roles"] = new[] { "Alerts.Write" },
        ["exp"] = 2000000000
    };

    private static ClaimsPrincipal Principal() => new(new ClaimsIdentity(
    [
        new("tid", Tenant), new("azp", Client), new("oid", ObjectId),
        new("iss", $"https://login.microsoftonline.com/{Tenant}/v2.0"),
        new("idtyp", "app"), new("roles", "Alerts.Write"), new("aud", Audience)
    ], "Bearer"));

    private static string Token(object payload) =>
        $"{Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "none", typ = "JWT" }))}." +
        $"{Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(payload))}.";

    private static DefaultHttpContext Context(string? token = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/alerts";
        context.Response.StatusCode = 400;
        if (token is not null)
            context.Request.Headers.Authorization = "Bearer " + token;
        return context;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class CompletingResponseFeature : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> callbacks = [];
        public override void OnCompleted(Func<object, Task> callback, object state) => callbacks.Add((callback, state));
        public async Task CompleteAsync()
        {
            foreach (var (callback, state) in callbacks.AsEnumerable().Reverse())
                await callback(state);
        }
    }

    private sealed class StubAuthenticationService : IAuthenticationService
    {
        public int Calls { get; private set; }
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        {
            Assert.Equal("Bearer", scheme);
            Calls++;
            return Task.FromResult(AuthenticateResult.NoResult());
        }
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }

    private sealed class SignalingDiagnostics(AlertIngestionDiagnostics inner) : IAlertIngestionDiagnostics
    {
        public TaskCompletionSource<AlertIngestionRequestDiagnostic> Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<AlertIngestionRequestDiagnostic> GetRecent() => inner.GetRecent();
        public void Record(HttpContext context, AuthenticateResult? authentication)
        {
            inner.Record(context, authentication);
            Completed.TrySetResult(Assert.Single(inner.GetRecent()));
        }
    }
}
