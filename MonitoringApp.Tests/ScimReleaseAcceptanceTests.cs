using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MonitoringApp.Tests;

public sealed class ScimReleaseAcceptanceTests
{
    private const string Username = "ap8-admin";
    private const string Password = "AP8-Synthetic-Local-Password-2026!";

    [ReleaseSqlFact]
    public async Task PublishedHostPreservesSqlLoginAndLegacyOwnershipWhileProvisioningGroupsTeamsAndRca()
    {
        var publish = Path.GetFullPath(Environment.GetEnvironmentVariable("MONITORINGAPP_SCIM_RELEASE_DIR")!);
        AssertPackage(publish);
        var database = "MonitoringApp_SCIM_AP8_" + Guid.NewGuid().ToString("N");
        var master = new SqlConnectionStringBuilder
        {
            DataSource = @"(localdb)\MSSQLLocalDB", InitialCatalog = "master", IntegratedSecurity = true, Encrypt = false
        };
        var connection = new SqlConnectionStringBuilder(master.ConnectionString) { InitialCatalog = database };
        var factory = new Factory(new DbContextOptionsBuilder<AlertDbContext>()
            .UseSqlServer(connection.ConnectionString, sql => sql.EnableRetryOnFailure()).Options);
        var scratch = Path.Combine(Path.GetTempPath(), database);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=MonitoringApp AP8 isolated test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var certificatePath = Path.Combine(scratch, "localhost.pfx");
        var certificatePassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var created = false;
        try
        {
            Directory.CreateDirectory(scratch);
            await File.WriteAllBytesAsync(certificatePath, certificate.Export(X509ContentType.Pfx, certificatePassword));
            await ExecuteAsync(master.ConnectionString, $"CREATE DATABASE [{database}];");
            created = true;
            var template = await File.ReadAllTextAsync(Path.Combine(publish, "Database", "CreateMonitoringDatabase.template.sql"));
            foreach (var batch in Regex.Split(template[template.IndexOf("/* SECTION 2:", StringComparison.Ordinal)..]
                .Replace("<database-name>", database), @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                if (!string.IsNullOrWhiteSpace(batch)) await ExecuteAsync(connection.ConnectionString, batch);
            await new SqlAuthenticationService(factory, new()).CreateUserAsync(Username, Password, "Admin");
            var collection = AnalysisDashboardCollection.Create(new() { Name = "AP8 retained dashboard" });
            await new AnalysisDashboardRepository(factory).SaveAsync(Username, collection, 0);
            var rcaId = Guid.NewGuid();
            await using (var context = factory.CreateDbContext())
            {
                (await context.Settings.SingleAsync(setting => setting.Name == DatabaseSettingsLoader.Authentication)).JsonValue = """{"type":"sql"}""";
                context.RootCauseAnalyses.Add(new()
                {
                    Id = rcaId, CategoryName = "AP8 provisioned assignment", CreatedAt = DateTimeOffset.UtcNow,
                    FollowUpDueAt = DateTimeOffset.UtcNow.AddHours(1), Revision = 1
                });
                await context.SaveChangesAsync();
            }

            var connectionId = Guid.NewGuid();
            var tenantId = Guid.NewGuid();
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            await using (var disabled = await PublishedHost.StartAsync(publish, connection.ConnectionString,
                certificatePath, certificatePassword, connectionId, tenantId, null))
            {
                using var browser = disabled.Client(certificate);
                await LoginAsync(browser);
                using var users = await browser.GetAsync("/admin/users");
                Assert.Equal(HttpStatusCode.OK, users.StatusCode);
                Assert.Contains(Username, await users.Content.ReadAsStringAsync());
                using var inventory = await browser.GetAsync("/admin/inventory");
                Assert.Equal(HttpStatusCode.OK, inventory.StatusCode);
                Assert.DoesNotContain("inventory-team-filter", await inventory.Content.ReadAsStringAsync());
                using var scim = await browser.GetAsync("/scim/v2/Users");
                Assert.Equal(HttpStatusCode.NotFound, scim.StatusCode);
                await using var check = factory.CreateDbContext();
                Assert.Empty(await check.ScimConnections.ToArrayAsync());
                Assert.Empty(await check.DirectoryUsers.ToArrayAsync());
            }

            await using (var enabled = await PublishedHost.StartAsync(publish, connection.ConnectionString,
                certificatePath, certificatePassword, connectionId, tenantId, token))
            {
                using var browser = enabled.Client(certificate);
                await LoginAsync(browser);
                using var anonymousMachine = enabled.Client(certificate);
                using (var denied = await anonymousMachine.GetAsync("/scim/v2/Users"))
                    Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
                using (var cookieIsNotToken = await browser.GetAsync("/scim/v2/Groups"))
                    Assert.Equal(HttpStatusCode.Unauthorized, cookieIsNotToken.StatusCode);
                using var machine = enabled.Client(certificate);
                machine.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var externalId = Guid.NewGuid();
                using var createdUser = await machine.PostAsJsonAsync("/scim/v2/Users",
                    ScimProtocolTests.User(externalId, "ap8-provisioned@example.test"));
                Assert.Equal(HttpStatusCode.Created, createdUser.StatusCode);
                var user = (await createdUser.Content.ReadFromJsonAsync<ScimUserResponse>())!;
                using var createdGroup = await machine.PostAsJsonAsync("/scim/v2/Groups",
                    ScimGroupTests.Body(Guid.NewGuid(), "AP8 source group", user.Id));
                Assert.Equal(HttpStatusCode.Created, createdGroup.StatusCode);
                var group = (await createdGroup.Content.ReadFromJsonAsync<ScimGroupResponse>())!;
                var auth = new ApplicationAuthenticationOptions { Type = "sql" };
                var directoryOptions = new IdentityDirectoryOptions { Enabled = true };
                var directory = new IdentityDirectoryService(factory, directoryOptions, auth);
                var actor = new ClaimsPrincipal(new ClaimsIdentity([
                    new(ClaimTypes.NameIdentifier, Username), new(ClaimTypes.Name, Username), new(ClaimTypes.Role, "Admin")
                ], ApplicationAuthorization.SqlCookieScheme));
                var actorId = DirectoryIdentity.UserId(await directory.ResolveAsync(actor))!.Value;
                var administration = new UserAdministrationService(factory, directory, auth,
                    new(factory, new(), directoryOptions), NullLogger<UserAdministrationService>.Instance);
                await administration.SaveTeamAsync(actor, null, null, "AP8 team", true);
                var snapshot = await administration.GetSnapshotAsync(actor);
                var team = snapshot.Teams.Single();
                var account = snapshot.Users.Single(account => account.Id == Guid.Parse(user.Id));
                await administration.SaveUserAsync(actor, new(account.Key, account.Version, null, true, [team.Id]));
                await administration.SaveGroupMappingAsync(actor, null, null, team.Id, Guid.Parse(group.Id), true);

                using var authorizationServices = new ServiceCollection().AddLogging()
                    .AddAuthorization(options => ApplicationAuthorization.Configure(options, auth)).BuildServiceProvider();
                var presenter = new QueryResultPresenter(Path.Combine(publish, "AlertDefinitions"));
                var rca = new RcaService(factory, authorizationServices.GetRequiredService<IAuthorizationService>(),
                    new(new HttpContextAccessor(), directory), auth, TimeProvider.System, new(presenter), presenter);
                var assignedKey = DirectoryIdentity.OwnerKey(Guid.Parse(user.Id));
                Assert.Contains(await rca.GetAssigneeOptionsAsync(actor), option => option.Key == assignedKey);
                await rca.SaveAsync(actor, rcaId, 1, "", assignedKey, "Open", "");
                using var users = await browser.GetAsync("/admin/users");
                var usersHtml = await users.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.OK, users.StatusCode);
                Assert.Contains("ap8-provisioned@example.test", usersHtml);
                Assert.Contains("AP8 source group", usersHtml);
                using var casePage = await browser.GetAsync("/rca?caseId=" + rcaId);
                Assert.Equal(HttpStatusCode.OK, casePage.StatusCode);
                Assert.Contains("value=\"" + assignedKey + "\"", await casePage.Content.ReadAsStringAsync());

                var migration = new IdentityMigrationService(factory, directory);
                var preview = await migration.PreviewAsync(actor, actorId, [Username]);
                Assert.Empty(preview.Conflicts);
                Assert.Equal(1, preview.DashboardCollections);
                await migration.CommitAsync(actor, actorId, [Username], preview.Fingerprint, "Synthetic isolated AP8 review");
                var stableOwner = DirectoryIdentity.OwnerKey(actorId);
                var restored = await new AnalysisDashboardRepository(factory, directoryOptions).LoadAsync(stableOwner);
                Assert.Equal(collection.ActiveDashboardId, restored.Collection!.ActiveDashboardId);
                await Assert.ThrowsAsync<IdentityMigrationException>(() =>
                    new AnalysisDashboardRepository(factory, directoryOptions).SaveAsync(Username, collection, 1));
                using var analysis = await browser.GetAsync("/analysis");
                Assert.Equal(HttpStatusCode.OK, analysis.StatusCode);
                Assert.Contains("AP8 retained dashboard", await analysis.Content.ReadAsStringAsync());

                using var deleted = await machine.DeleteAsync("/scim/v2/Users/" + user.Id);
                Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
                var retained = (await administration.GetSnapshotAsync(actor)).Users.Single(account => account.Id == Guid.Parse(user.Id));
                Assert.True(Assert.Single(retained.TeamSources).Manual);
                Assert.DoesNotContain(await rca.GetAssigneeOptionsAsync(actor), option => option.Key == assignedKey);
                Assert.Equal(assignedKey, Assert.Single(await rca.ListAsync()).AssignedTo);
                await using var check = factory.CreateDbContext();
                Assert.False((await check.DirectoryUsers.SingleAsync(entry => entry.Id == Guid.Parse(user.Id))).UsesStableIdentity);
                Assert.Single(await check.IdentityMigrationRecords.ToArrayAsync());
                Assert.Equal(stableOwner, (await check.AnalysisDashboards.SingleAsync()).OwnerKey);
            }

            await using (var paused = await PublishedHost.StartAsync(publish, connection.ConnectionString,
                certificatePath, certificatePassword, connectionId, tenantId, null, directoryEnabled: true))
            {
                using var browser = paused.Client(certificate);
                await LoginAsync(browser);
                using var scim = await browser.GetAsync("/scim/v2/Users");
                Assert.Equal(HttpStatusCode.NotFound, scim.StatusCode);
                using var users = await browser.GetAsync("/admin/users");
                Assert.Equal(HttpStatusCode.OK, users.StatusCode);
                Assert.Contains("ap8-provisioned@example.test", await users.Content.ReadAsStringAsync());
                using var analysis = await browser.GetAsync("/analysis");
                Assert.Equal(HttpStatusCode.OK, analysis.StatusCode);
                Assert.Contains("AP8 retained dashboard", await analysis.Content.ReadAsStringAsync());
                await using var check = factory.CreateDbContext();
                Assert.True((await check.ScimConnections.SingleAsync()).IsEnabled);
                Assert.True((await check.ScimUserStates.SingleAsync()).IsDeleted);
                Assert.Single(await check.UserTeamMemberships.ToArrayAsync());
            }
            var historicalCaseId = Guid.NewGuid();
            var aliasCaseId = Guid.NewGuid();
            await using (var context = factory.CreateDbContext())
            {
                (await context.Settings.SingleAsync(setting => setting.Name == DatabaseSettingsLoader.Authentication)).JsonValue = """{"type":"open"}""";
                context.RootCauseAnalyses.Add(new()
                {
                    Id = historicalCaseId, AssignedTo = "ap9-historical@example.test", CategoryName = "Historical profile acceptance",
                    CreatedAt = DateTimeOffset.UtcNow, FollowUpDueAt = DateTimeOffset.UtcNow.AddHours(1), Revision = 1
                });
                context.RootCauseAnalyses.Add(new()
                {
                    Id = aliasCaseId, AssignedTo = "ap9-alias@example.test", CategoryName = "Historical alias acceptance",
                    CreatedAt = DateTimeOffset.UtcNow, FollowUpDueAt = DateTimeOffset.UtcNow.AddHours(1), Revision = 1
                });
                await context.SaveChangesAsync();
            }
            await using (var open = await PublishedHost.StartAsync(publish, connection.ConnectionString,
                certificatePath, certificatePassword, connectionId, tenantId, null, directoryEnabled: true))
            {
                var openAuth = new ApplicationAuthenticationOptions { Type = "open" };
                var directoryOptions = new IdentityDirectoryOptions { Enabled = true };
                var administration = new UserAdministrationService(factory, new(factory, directoryOptions, openAuth), openAuth,
                    new(factory, new(), directoryOptions), NullLogger<UserAdministrationService>.Instance);
                var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
                var historical = (await administration.GetSnapshotAsync(anonymous)).Users.Single(user =>
                    user.IsHistorical && user.UserName == "ap9-historical@example.test");
                Assert.Null(historical.Id);
                async Task<string> SaveProfileAsync(string name)
                {
                    try
                    {
                        await administration.SaveUserAsync(anonymous, new(historical.Key, historical.Version,
                            "AP9 historical display", true, [], new(name, "Local first", "Local last", "local-contact@example.test")));
                        return "Saved";
                    }
                    catch (UserAdministrationException exception) { return exception.ResourceKey; }
                }
                var saves = await Task.WhenAll(SaveProfileAsync("Profile one"), SaveProfileAsync("Profile two"));
                Assert.Single(saves, result => result == "Saved");
                Assert.Single(saves, result => result == "UserDirectory.Errors.Stale");
                using var browser = open.Client(certificate);
                using var users = await browser.GetAsync("/admin/users");
                Assert.Equal(HttpStatusCode.OK, users.StatusCode);
                var html = await users.Content.ReadAsStringAsync();
                Assert.Contains("Open mode:", html);
                Assert.Contains("Inventory server roles", html);
                Assert.Contains("AP8 team", html);
                Assert.Contains("AP9 historical display", html);
                Assert.Contains("Unverified / not linked", html);
                Assert.DoesNotContain("Stable identities and existing data", html);
                using var inventory = await browser.GetAsync("/admin/inventory");
                Assert.Equal(HttpStatusCode.OK, inventory.StatusCode);
                var inventoryHtml = await inventory.Content.ReadAsStringAsync();
                Assert.Contains("inventory-team-filter", inventoryHtml);
                Assert.Contains("Teams (optional view filter)", inventoryHtml);
                Assert.Contains("AP8 team", inventoryHtml);
                using var historicalCase = await browser.GetAsync("/rca?caseId=" + historicalCaseId);
                Assert.Equal(HttpStatusCode.OK, historicalCase.StatusCode);
                Assert.Contains("AP9 historical display", await historicalCase.Content.ReadAsStringAsync());
                await using (var check = factory.CreateDbContext())
                {
                    var profile = await check.DirectoryUsers.SingleAsync(user => user.Origin == DirectoryUserOrigins.Legacy &&
                        user.UserName == "ap9-historical@example.test");
                    Assert.False(profile.UsesStableIdentity);
                    Assert.Equal("Local first", profile.GivenName);
                    Assert.Equal("Local last", profile.FamilyName);
                    Assert.Equal("local-contact@example.test", profile.Email);
                    Assert.False(await check.EntraUserIdentities.AnyAsync(identity => identity.UserId == profile.Id));
                    Assert.False(await check.SqlUserIdentities.AnyAsync(identity => identity.UserId == profile.Id));
                    Assert.False(await check.ScimUserStates.AnyAsync(state => state.UserId == profile.Id));
                    Assert.Equal("ap9-historical@example.test",
                        (await check.RootCauseAnalyses.SingleAsync(row => row.Id == historicalCaseId)).AssignedTo);
                    var audit = await check.IdentityAdministrationRecords.SingleAsync(record => record.TargetUserId == profile.Id);
                    Assert.True(audit.IsOpenAuthentication);
                    Assert.Null(audit.ActorUserId);
                }
                var alias = (await administration.GetSnapshotAsync(anonymous)).Users.Single(user =>
                    user.IsHistorical && user.UserName == "ap9-alias@example.test");
                await administration.SaveUserAsync(anonymous, new(alias.Key, alias.Version, "AP9 alias original", true, []));
                historical = (await administration.GetSnapshotAsync(anonymous)).Users.Single(user => user.Key == historical.Key);
                await administration.SaveUserAsync(anonymous, new(historical.Key, historical.Version,
                    historical.LocalDisplayName, true, [], HistoricalAliasKeys: [alias.Key]));
                using (var groupedUsers = await browser.GetAsync("/admin/users"))
                {
                    Assert.Equal(HttpStatusCode.OK, groupedUsers.StatusCode);
                    var groupedHtml = await groupedUsers.Content.ReadAsStringAsync();
                    Assert.Contains("AP9 historical display", groupedHtml);
                    Assert.Contains("ap9-alias@example.test", groupedHtml);
                }
                using (var groupedCase = await browser.GetAsync("/rca?caseId=" + aliasCaseId))
                {
                    Assert.Equal(HttpStatusCode.OK, groupedCase.StatusCode);
                    Assert.Contains("AP9 historical display (ap9-alias@example.test)", await groupedCase.Content.ReadAsStringAsync());
                }
                await using (var check = factory.CreateDbContext())
                {
                    Assert.Equal("ap9-alias@example.test",
                        (await check.RootCauseAnalyses.SingleAsync(row => row.Id == aliasCaseId)).AssignedTo);
                    Assert.Equal("AP9 alias original", (await check.DirectoryUsers.SingleAsync(user => user.UserName == "ap9-alias@example.test")).LocalDisplayName);
                }
                alias = (await administration.GetSnapshotAsync(anonymous)).Users.Single(user => user.Key == alias.Key);
                await administration.DetachHistoricalAliasAsync(anonymous, alias.Key, alias.Version);
                using (var separatedCase = await browser.GetAsync("/rca?caseId=" + aliasCaseId))
                {
                    Assert.Equal(HttpStatusCode.OK, separatedCase.StatusCode);
                    Assert.Contains("AP9 alias original (ap9-alias@example.test)", await separatedCase.Content.ReadAsStringAsync());
                }
                using var scim = await browser.GetAsync("/scim/v2/Users");
                Assert.Equal(HttpStatusCode.NotFound, scim.StatusCode);
            }
        }
        finally
        {
            try
            {
                if (created)
                {
                    using var pool = new SqlConnection(connection.ConnectionString);
                    SqlConnection.ClearPool(pool);
                    await ExecuteAsync(master.ConnectionString,
                        $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
                }
            }
            finally
            {
                File.Delete(certificatePath);
                if (Directory.Exists(scratch)) Directory.Delete(scratch);
            }
        }
    }

    private static void AssertPackage(string directory)
    {
        foreach (var name in new[] { "MonitoringApp.dll", "MonitoringApp.runtimeconfig.json",
            @"wwwroot\js\kql-completion.json", @"Database\UpgradeIdentityDirectory.sql", @"Database\UpgradeIdentityActivation.sql",
            @"Database\UpgradeScimUserLifecycle.sql", @"Database\UpgradeIdentityAdministration.sql", @"Database\UpgradeScimGroupLifecycle.sql",
            @"Database\UpgradeOpenAdministrationAndTeamInventoryRoles.sql", @"Database\UpgradeHistoricalUserAliases.sql" })
            Assert.True(File.Exists(Path.Combine(directory, name)), "Missing release file: " + name);
        Assert.False(File.Exists(Path.Combine(directory, "appsettings.Development.json")));
        Assert.False(Directory.Exists(Path.Combine(directory, "config")));
        Assert.False(Directory.Exists(Path.Combine(directory, "doc")));
        Assert.DoesNotContain(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories),
            path => new[] { ".pfx", ".p12", ".pem", ".key" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "appsettings.json")));
        Assert.False(settings.RootElement.TryGetProperty("Scim", out _));
        Assert.False(settings.RootElement.TryGetProperty("ConnectionStrings", out _));
    }

    private static async Task LoginAsync(HttpClient client)
    {
        using var login = await client.GetAsync("/login");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var html = await login.Content.ReadAsStringAsync();
        var match = Regex.Match(html, """<input\b(?=[^>]*\bname="__RequestVerificationToken")(?=[^>]*\bvalue="(?<token>[^"]+)")[^>]*>""");
        Assert.True(match.Success, "The published login page did not emit an antiforgery token.");
        using var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = Username, ["Password"] = Password, ["ReturnUrl"] = "/admin/users",
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups["token"].Value)
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin/users", response.Headers.Location!.OriginalString);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class Factory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        public AlertDbContext CreateDbContext() => new(options);
    }

    private sealed class PublishedHost(Process process, Uri address) : IAsyncDisposable
    {
        public HttpClient Client(X509Certificate2 certificate)
        {
            var client = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (_, presented, _, _) =>
                    presented?.Thumbprint == certificate.Thumbprint
            }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");
            return client;
        }

        public static async Task<PublishedHost> StartAsync(string publish, string connection,
            string certificate, string certificatePassword, Guid connectionId, Guid tenantId, string? token,
            bool directoryEnabled = false)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = publish, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            var inherited = start.Environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            start.Environment.Clear();
            foreach (var key in new[] { "SystemRoot", "WINDIR", "PATH", "TEMP", "TMP", "USERPROFILE", "LOCALAPPDATA",
                "APPDATA", "HOMEDRIVE", "HOMEPATH", "PROGRAMFILES", "PROGRAMFILES(X86)", "PROGRAMDATA", "DOTNET_ROOT",
                "COMSPEC", "USERNAME", "USERDOMAIN" })
                if (inherited.TryGetValue(key, out var value)) start.Environment[key] = value;
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            foreach (var argument in new[] { Path.Combine(publish, "MonitoringApp.dll"), "--urls", "https://127.0.0.1:0",
                "--ConnectionStrings:AlertsDatabase=" + connection, "--APPLICATIONINSIGHTS_CONNECTION_STRING=",
                "--Kestrel:Certificates:Default:Path=" + certificate, "--Kestrel:Certificates:Default:Password=" + certificatePassword,
                "--IdentityDirectory:Enabled=" + (directoryEnabled || token is not null), "--Scim:Enabled=" + (token is not null),
                "--AlertIngestionAuthentication:Enabled=false" })
                start.ArgumentList.Add(argument);
            if (token is not null)
                foreach (var argument in new[] { "--Scim:ConnectionId=" + connectionId, "--Scim:TenantId=" + tenantId,
                    "--Scim:Name=AP8 isolated source", "--Scim:BaseUrl=https://provisioning.example.test/scim/v2",
                    "--Scim:TokenHashes:0=" + ScimTestHost.Hash(token) })
                    start.ArgumentList.Add(argument);
            var process = new Process { StartInfo = start };
            var ready = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            var output = new ConcurrentQueue<string>();
            void Read(object sender, DataReceivedEventArgs args)
            {
                if (args.Data is not { } line) return;
                output.Enqueue(line.Replace(certificatePassword, "[redacted]", StringComparison.Ordinal));
                while (output.Count > 30) output.TryDequeue(out _);
                var listening = Regex.Match(line, @"Now listening on:\s*(https://127\.0\.0\.1:\d+)");
                if (listening.Success) ready.TrySetResult(new Uri(listening.Groups[1].Value));
            }
            process.OutputDataReceived += Read;
            process.ErrorDataReceived += Read;
            if (!process.Start()) throw new InvalidOperationException("The isolated published host did not start.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var completed = await Task.WhenAny(ready.Task, process.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(60)));
            if (completed == ready.Task) return new(process, await ready.Task);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            process.Dispose();
            throw new InvalidOperationException("Isolated published host failed to become ready: " + string.Join(Environment.NewLine, output));
        }

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            process.Dispose();
        }
    }

    private sealed class ReleaseSqlFactAttribute : FactAttribute
    {
        public ReleaseSqlFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("MONITORINGAPP_SCIM_SQL_TESTS") != "1" ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MONITORINGAPP_SCIM_RELEASE_DIR")))
                Skip = "Opt-in isolated published-host test: set MONITORINGAPP_SCIM_SQL_TESTS=1 and MONITORINGAPP_SCIM_RELEASE_DIR.";
        }
    }
}
