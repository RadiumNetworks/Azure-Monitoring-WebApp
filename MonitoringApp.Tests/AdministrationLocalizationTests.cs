using System.Globalization;
using System.Data.Common;
using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components.Pages;

namespace MonitoringApp.Tests;

public sealed class AdministrationLocalizationTests
{
    private static string Password => AuthenticationTestData.Cases.Password;

    [Theory]
    [InlineData("en-US", "sql")]
    [InlineData("de-DE", "sql")]
    [InlineData("en-US", "entra")]
    [InlineData("de-DE", "entra")]
    [InlineData("en-US", "open")]
    [InlineData("de-DE", "open")]
    public async Task UserPagePreservesIdentityRoleValuesAndSafeWarningLinks(string culture, string mode)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create(mode);
        await fixture.RenderUsers();
        Assert.Contains(fixture.Text["AdminUsers.Title"], fixture.Decoded);
        Assert.Contains("Reader", fixture.Html);
        Assert.Contains("Original &lt;user&gt;", fixture.Html);
        await fixture.Invoke("BeginEdit", new SqlAuthenticationUserSummary("Original <user>", "Reader"));
        Assert.Contains("value=\"Reader\"", fixture.Html);
        Assert.Contains("value=\"Operator\"", fixture.Html);
        Assert.Contains("value=\"Admin\"", fixture.Html);
        Assert.Contains(fixture.Text["AdminUsers.PasswordOptionalHint"], fixture.Decoded);
        Assert.DoesNotContain(" required", fixture.Html.Split("type=\"password\"", StringSplitOptions.None)[1].Split('>')[0]);
        if (mode == "open")
        {
            Assert.Contains("href=\"/admin/settings\"", fixture.Html);
            Assert.Contains("<strong>sql</strong>", fixture.Html);
            Assert.Contains(fixture.Text["AdminUsers.OpenActive"], fixture.Decoded);
        }
        if (mode == "entra") Assert.Contains(fixture.Text["AdminUsers.EntraActive"], fixture.Decoded);
        await fixture.Invoke("BeginDelete", "Original <user>");
        Assert.Contains("<strong>Original &lt;user&gt;</strong>", fixture.Html);
        Assert.Contains(fixture.Text["AdminUsers.DeleteTitle"], fixture.Decoded);
        Assert.DoesNotContain("<user>", fixture.Html);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task RoleOnlyChangeLeavesExactPasswordHashAndCredentialsUnchanged(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        var original = await fixture.ReadUser();
        await fixture.RenderUsers();
        await fixture.Invoke("BeginEdit", new SqlAuthenticationUserSummary(original.Username, original.Role));
        fixture.Set("editingRole", SqlAuthenticationRoles.Operator);
        await fixture.Invoke("SaveAsync");
        var updated = await fixture.ReadUser();
        Assert.Equal(SqlAuthenticationRoles.Operator, updated.Role);
        Assert.Equal(original.PasswordHash, updated.PasswordHash);
        Assert.NotNull(await fixture.Service.ValidateCredentialsAsync(original.Username, Password));
        Assert.Contains(fixture.Text.Format("AdminUsers.Updated", original.Username), fixture.Decoded);
    }

    [Theory]
    [InlineData("en-US", "password")]
    [InlineData("de-DE", "password")]
    [InlineData("en-US", "confirmation")]
    [InlineData("de-DE", "confirmation")]
    [InlineData("en-US", "mismatch")]
    [InlineData("de-DE", "mismatch")]
    public async Task OneSidedOrMismatchedPasswordsNeverChangeRoleOrHash(string culture, string input)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        var original = await fixture.ReadUser();
        await fixture.RenderUsers();
        await fixture.Invoke("BeginEdit", new SqlAuthenticationUserSummary(original.Username, original.Role));
        fixture.Set("editingRole", SqlAuthenticationRoles.Operator);
        fixture.Set("password", input == "confirmation" ? "" : Password);
        fixture.Set("passwordConfirmation", input == "password" ? "" : Password + (input == "mismatch" ? "x" : ""));
        await fixture.Invoke("SaveAsync");
        var actual = await fixture.ReadUser();
        Assert.Equal(original.PasswordHash, actual.PasswordHash);
        Assert.Equal(original.Role, actual.Role);
        Assert.Contains(fixture.Text["AdminUsers.PasswordMismatch"], fixture.Decoded);
        Assert.True(fixture.Html.Split("type=\"password\"", StringSplitOptions.None).Skip(1)
            .All(fragment => fragment.Split('>')[0].Contains("required", StringComparison.Ordinal)));
        Assert.DoesNotContain(Password, fixture.Error);
        Assert.False(fixture.Get<bool>("reloadRequired"));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task ConfirmedPasswordChangeReplacesCredentialsAndClearsPasswordInputs(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        var original = await fixture.ReadUser();
        await fixture.RenderUsers();
        await fixture.Invoke("BeginEdit", new SqlAuthenticationUserSummary(original.Username, original.Role));
        fixture.Set("password", Password + "-changed");
        fixture.Set("passwordConfirmation", Password + "-changed");
        await fixture.Invoke("SaveAsync");
        Assert.Null(await fixture.Service.ValidateCredentialsAsync(original.Username, Password));
        Assert.NotNull(await fixture.Service.ValidateCredentialsAsync(original.Username, Password + "-changed"));
        Assert.DoesNotContain("type=\"password\"", fixture.Html);
        Assert.Contains(fixture.Text.Format("AdminUsers.Updated", original.Username), fixture.Decoded);
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task CreationKeepsPasswordRequiredAndExistingLimits(string culture, bool valid)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await fixture.RenderUsers();
        await fixture.Invoke("BeginCreate");
        Assert.True(fixture.Html.Split("type=\"password\"", StringSplitOptions.None).Skip(1)
            .All(fragment => fragment.Split('>')[0].Contains("required", StringComparison.Ordinal)));
        Assert.Contains("maxlength=\"128\"", fixture.Html);
        Assert.Contains("maxlength=\"1024\"", fixture.Html);
        fixture.Set("editingUsername", "new user");
        fixture.Set("password", valid ? Password : "");
        fixture.Set("passwordConfirmation", valid ? Password : "");
        await fixture.Invoke("SaveAsync");
        if (valid)
        {
            Assert.NotNull(await fixture.Service.ValidateCredentialsAsync("new user", Password));
            Assert.Contains(fixture.Text.Format("AdminUsers.Created", "new user"), fixture.Decoded);
        }
        else
        {
            Assert.Equal(2, (await fixture.Service.GetUsersAsync()).Count);
            Assert.Contains(fixture.Text["AdminUsers.Errors.Password"], fixture.Decoded);
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task PartialRoleWriteAndPasswordFailureNeverClaimRollbackOrSuccess(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        var original = await fixture.ReadUser();
        await fixture.RenderUsers();
        await fixture.Execute("CREATE TRIGGER FailPassword BEFORE UPDATE OF PasswordHash ON AuthenticationUsers BEGIN SELECT RAISE(ABORT, 'PRIVATE_PROVIDER_DETAIL'); END;");
        await fixture.Invoke("BeginEdit", new SqlAuthenticationUserSummary(original.Username, original.Role));
        fixture.Set("editingRole", SqlAuthenticationRoles.Operator);
        fixture.Set("password", Password + "-changed");
        fixture.Set("passwordConfirmation", Password + "-changed");
        await fixture.Invoke("SaveAsync");
        var actual = await fixture.ReadUser();
        Assert.Equal(SqlAuthenticationRoles.Operator, actual.Role);
        Assert.Equal(original.PasswordHash, actual.PasswordHash);
        Assert.Contains(fixture.Text["AdminUsers.Errors.RoleSavedPasswordUnconfirmed"], fixture.Decoded);
        Assert.DoesNotContain("admin-message-success", fixture.Html);
        Assert.DoesNotContain("PRIVATE_PROVIDER_DETAIL", fixture.Html);
        Assert.DoesNotContain(Password, fixture.Error);
        Assert.True(fixture.Get<bool>("reloadRequired"));
        var calls = fixture.Factory.CreationCount;
        await fixture.Invoke("SaveAsync");
        Assert.Equal(calls, fixture.Factory.CreationCount);
        Assert.Equal(original.PasswordHash, (await fixture.ReadUser()).PasswordHash);
        await fixture.Invoke("LoadUsersAsync");
        Assert.False(fixture.Get<bool>("reloadRequired"));
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task ConfirmedMutationWithFailedRefreshShowsOnlySpecificWarning(string culture, bool deleting)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await fixture.RenderUsers();
        var user = await fixture.ReadUser();
        fixture.Factory.FailReadAfterWrite = true;
        if (deleting) await fixture.Invoke("BeginDelete", user.Username);
        else
        {
            await fixture.Invoke("BeginEdit", new SqlAuthenticationUserSummary(user.Username, user.Role));
            fixture.Set("password", Password + "-changed");
            fixture.Set("passwordConfirmation", Password + "-changed");
        }
        await fixture.Invoke(deleting ? "DeleteAsync" : "SaveAsync");
        Assert.Contains(fixture.Text[deleting ? "AdminUsers.Errors.DeletedRefreshFailed" : "AdminUsers.Errors.SavedRefreshFailed"], fixture.Decoded);
        Assert.DoesNotContain("admin-message-success", fixture.Html);
        Assert.DoesNotContain("PRIVATE_PROVIDER_DETAIL", fixture.Html);
        fixture.Factory.FailReadAfterWrite = false;
        fixture.Factory.FailCreation = false;
        if (deleting) Assert.Single(await fixture.Service.GetUsersAsync());
        else Assert.NotNull(await fixture.Service.ValidateCredentialsAsync(user.Username, Password + "-changed"));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task KnownCodesHaveCompleteMessagesAndUnknownLoadAndWriteErrorsStayPrivate(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        foreach (var code in Enum.GetValues<SqlAuthenticationErrorCode>())
            Assert.DoesNotContain("AdminUsers.Errors.", fixture.Text.Format("AdminUsers.Errors." + code, "Original <user>"));
        await fixture.RenderUsers();
        fixture.Factory.FailCreation = true;
        await fixture.Invoke("LoadUsersAsync");
        Assert.Contains(fixture.Text["AdminUsers.Errors.LoadFailed"], fixture.Decoded);
        await fixture.Invoke("BeginEdit", new SqlAuthenticationUserSummary("Original <user>", "Reader"));
        await fixture.Invoke("SaveAsync");
        Assert.Contains(fixture.Text["AdminUsers.Errors.ChangeUnconfirmed"], fixture.Decoded);
        fixture.Factory.FailCreation = false;
        await fixture.Invoke("LoadUsersAsync");
        await fixture.Invoke("BeginDelete", "Original <user>");
        fixture.Factory.FailCreation = true;
        await fixture.Invoke("DeleteAsync");
        Assert.Contains(fixture.Text["AdminUsers.Errors.ChangeUnconfirmed"], fixture.Decoded);
        Assert.DoesNotContain("PRIVATE_PROVIDER_DETAIL", fixture.Html);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task InitialUserLoadFailureIsNotAnEmptyListAndCannotEnableWrites(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        fixture.Factory.FailCreation = true;
        await fixture.RenderUsers();
        Assert.Contains(fixture.Text["AdminUsers.Errors.LoadFailed"], fixture.Decoded);
        Assert.DoesNotContain(fixture.Text["AdminUsers.Empty"], fixture.Decoded);
        Assert.DoesNotContain(fixture.Text.Plural("AdminUsers.Count", 0), fixture.Decoded);
        var calls = fixture.Factory.CreationCount;
        await fixture.Invoke("BeginCreate");
        await fixture.Invoke("BeginEdit", new SqlAuthenticationUserSummary("Original <user>", "Reader"));
        await fixture.Invoke("BeginDelete", "Original <user>");
        await fixture.Invoke("SaveAsync");
        await fixture.Invoke("DeleteAsync");
        Assert.Equal(calls, fixture.Factory.CreationCount);
        Assert.Null(fixture.Get<string?>("editingUsername"));
        fixture.Factory.FailCreation = false;
        await fixture.Invoke("LoadUsersAsync");
        await fixture.Invoke("BeginCreate");
        Assert.Equal(string.Empty, fixture.Get<string>("editingUsername"));
        Assert.Equal(SqlAuthenticationRoles.Reader, fixture.Get<string>("editingRole"));
        Assert.Equal(2, (await fixture.Service.GetUsersAsync()).Count);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task UnknownWrappedAndMalformedUserCodesUseSafeFallbackAndRequireExplicitReload(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await fixture.RenderUsers();
        var failures = new Exception[]
        {
            new SqlAuthenticationException((SqlAuthenticationErrorCode)999, "PRIVATE_PROVIDER_DETAIL"),
            new SqlAuthenticationException(SqlAuthenticationErrorCode.DuplicateUser, "PRIVATE_PROVIDER_DETAIL"),
            new SqlAuthenticationException(SqlAuthenticationErrorCode.DuplicateUser, "PRIVATE_PROVIDER_DETAIL", arguments: [123]),
            new InvalidOperationException("PRIVATE_PROVIDER_DETAIL",
                new SqlAuthenticationException(SqlAuthenticationErrorCode.LastAdmin, "PRIVATE_PROVIDER_DETAIL"))
        };
        foreach (var failure in failures)
        {
            foreach (var deleting in new[] { false, true })
            {
                await fixture.Invoke("BeginEdit", new SqlAuthenticationUserSummary("Original <user>", "Reader"));
                if (deleting) await fixture.Invoke("BeginDelete", "Original <user>");
                fixture.Factory.Failure = failure;
                await fixture.Invoke(deleting ? "DeleteAsync" : "SaveAsync");
                Assert.Equal(fixture.Text["AdminUsers.Errors.ChangeUnconfirmed"], fixture.Error);
                Assert.Contains(fixture.Text["AdminUsers.Errors.ReloadRequired"], fixture.Decoded);
                Assert.DoesNotContain("PRIVATE_PROVIDER_DETAIL", fixture.Html);
                Assert.True(fixture.Get<bool>("reloadRequired"));
                var calls = fixture.Factory.CreationCount;
                await fixture.Invoke("SaveAsync");
                await fixture.Invoke("BeginDelete", "Original <user>");
                await fixture.Invoke("DeleteAsync");
                Assert.Equal(calls, fixture.Factory.CreationCount);
                await fixture.Invoke("LoadUsersAsync");
                Assert.True(fixture.Get<bool>("reloadRequired"));
                fixture.Factory.Failure = null;
                await fixture.Invoke("LoadUsersAsync");
                Assert.False(fixture.Get<bool>("reloadRequired"));
                Assert.Equal(2, (await fixture.Service.GetUsersAsync()).Count);
            }
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task DirectKnownUserCodesHaveWholeMessagesAndOnlyUnconfirmedCodesLockRetry(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await fixture.RenderUsers();
        foreach (var code in Enum.GetValues<SqlAuthenticationErrorCode>())
        {
            await fixture.Invoke("BeginEdit", new SqlAuthenticationUserSummary("Original <user>", "Reader"));
            var failure = new SqlAuthenticationException(code, "PRIVATE_PROVIDER_DETAIL",
                arguments: code == SqlAuthenticationErrorCode.DuplicateUser ? ["Original <user>"] : []);
            fixture.Factory.Failure = failure;
            await fixture.Invoke("SaveAsync");
            Assert.Equal(fixture.Text.Format("AdminUsers.Errors." + code, "Original <user>"), fixture.Error);
            Assert.Equal(code is SqlAuthenticationErrorCode.CreateUnconfirmed or
                SqlAuthenticationErrorCode.PasswordUnconfirmed or SqlAuthenticationErrorCode.DeleteUnconfirmed,
                fixture.Get<bool>("reloadRequired"));
            Assert.Equal("PRIVATE_PROVIDER_DETAIL", failure.Message);
            Assert.DoesNotContain("PRIVATE_PROVIDER_DETAIL", fixture.Html);
            if (code == SqlAuthenticationErrorCode.DuplicateUser)
                Assert.Contains("Original &lt;user&gt;", fixture.Html);
            fixture.Factory.Failure = null;
            await fixture.Invoke("LoadUsersAsync");
            Assert.Equal(2, (await fixture.Service.GetUsersAsync()).Count);
        }
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task ActualPostCommitUserFailuresBlockRepeatsUntilSuccessfulReload(string culture, bool deleting)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await fixture.RenderUsers();
        if (deleting) await fixture.Invoke("BeginDelete", "Original <user>");
        else
        {
            await fixture.Invoke("BeginCreate");
            fixture.Set("editingUsername", "Created <user>");
            fixture.Set("password", Password);
            fixture.Set("passwordConfirmation", Password);
        }
        fixture.Factory.ThrowAfterWrite = true;
        await fixture.Invoke(deleting ? "DeleteAsync" : "SaveAsync");
        Assert.True(fixture.Get<bool>("reloadRequired"));
        Assert.DoesNotContain("admin-message-success", fixture.Html);
        Assert.DoesNotContain("PRIVATE_PROVIDER_DETAIL", fixture.Html);
        var calls = fixture.Factory.CreationCount;
        await fixture.Invoke("SaveAsync");
        await fixture.Invoke("DeleteAsync");
        Assert.Equal(calls, fixture.Factory.CreationCount);
        if (deleting) Assert.Null(fixture.Get<string?>("usernameToDelete"));
        fixture.Factory.ThrowAfterWrite = false;
        fixture.Factory.FailCreation = true;
        await fixture.Invoke("LoadUsersAsync");
        Assert.True(fixture.Get<bool>("reloadRequired"));
        fixture.Factory.FailCreation = false;
        await fixture.Invoke("LoadUsersAsync");
        Assert.False(fixture.Get<bool>("reloadRequired"));
        var users = await fixture.Service.GetUsersAsync();
        Assert.Equal(deleting ? 1 : 3, users.Count);
        if (!deleting)
        {
            Assert.Contains(users, user => user.Username == "Created <user>");
            Assert.Contains("Created &lt;user&gt;", fixture.Html);
            Assert.NotNull(await fixture.Service.ValidateCredentialsAsync("Created <user>", Password));
        }
    }

    [Theory]
    [InlineData("en-US", "LoadUsersAsync")]
    [InlineData("de-DE", "LoadUsersAsync")]
    [InlineData("en-US", "SaveAsync")]
    [InlineData("de-DE", "SaveAsync")]
    [InlineData("en-US", "DeleteAsync")]
    [InlineData("de-DE", "DeleteAsync")]
    public async Task BusyUserOperationsPreserveDraftAndPreventConcurrentActions(string culture, string operation)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await fixture.RenderUsers();
        await fixture.Invoke("BeginEdit", new SqlAuthenticationUserSummary("Original <user>", "Reader"));
        if (operation == "DeleteAsync") await fixture.Invoke("BeginDelete", "Original <user>");
        fixture.Factory.BlockNextCreation = true;
        var pending = fixture.Invoke(operation);
        await fixture.Factory.CreationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var calls = fixture.Factory.CreationCount;
            await fixture.Invoke("BeginCreate");
            await fixture.Invoke("BeginEdit", new SqlAuthenticationUserSummary("administrator", "Admin"));
            await fixture.Invoke("BeginDelete", "administrator");
            await fixture.Invoke("CancelEdit");
            await fixture.Invoke("CancelDelete");
            await fixture.Invoke("LoadUsersAsync");
            await fixture.Invoke("SaveAsync");
            await fixture.Invoke("DeleteAsync");
            Assert.Equal(calls, fixture.Factory.CreationCount);
            Assert.Equal("Original <user>", fixture.Get<string>("editingUsername"));
            Assert.Equal(SqlAuthenticationRoles.Reader, fixture.Get<string>("editingRole"));
            Assert.Equal(operation == "DeleteAsync" ? "Original <user>" : null, fixture.Get<string?>("usernameToDelete"));
            Assert.Contains("inert", fixture.Html);
        }
        finally
        {
            fixture.Factory.ReleaseCreation.SetResult();
            await pending;
        }
        Assert.Equal(operation == "DeleteAsync" ? 1 : 2, (await fixture.Service.GetUsersAsync()).Count);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task LogAnalyticsChromeUsesCorrectPageInstanceTextAndPreservesTechnicalSettings(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        var html = await fixture.Render<AdminLogAnalytics>();
        Assert.Contains(fixture.Text["LogAnalyticsAdmin.NoReport"], WebUtility.HtmlDecode(html));
        Assert.Contains("<code>CrossTenantLogAnalytics:Enabled</code>", html);
        Assert.Contains("<code>true</code>", html);
        Assert.Contains(fixture.Text["LogAnalyticsAdmin.Run"], WebUtility.HtmlDecode(html));
        Assert.Equal("Checked 2026-11-06 12:00:00 UTC", fixture.Text.Format("LogAnalyticsAdmin.CheckedAt", "2026-11-06 12:00:00")
            .Replace("Geprüft am", "Checked", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task TypedServiceValidationDoesNotWriteOrLeakCredentialValues(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        foreach (var (username, password, role, expected) in new[]
        {
            (" ", Password, "Reader", SqlAuthenticationErrorCode.Username),
            (new string('u', 129), Password, "Reader", SqlAuthenticationErrorCode.Username),
            ("new", "", "Reader", SqlAuthenticationErrorCode.Password),
            ("new", new string('p', 1025), "Reader", SqlAuthenticationErrorCode.Password),
            ("new", Password, "invalid", SqlAuthenticationErrorCode.Role),
            (" Original <user> ", Password, "Reader", SqlAuthenticationErrorCode.DuplicateUser)
        })
        {
            var error = await Assert.ThrowsAsync<SqlAuthenticationException>(() => fixture.Service.CreateUserAsync(username, password, role));
            Assert.Equal(expected, error.Code);
            Assert.DoesNotContain(Password, error.Message);
            Assert.DoesNotContain(Password, string.Join(" ", error.Arguments));
            Assert.Equal(2, (await fixture.Service.GetUsersAsync()).Count);
        }
    }

    private sealed class UnusedSecret : ILogAnalyticsSecretProvider
    {
        public Task<string> GetClientSecretAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The disabled diagnostic must not access secrets.");
    }

    private sealed class UnusedProbe : ILogAnalyticsWorkspaceProbe
    {
        public Task<LogAnalyticsWorkspaceProbeResult> ProbeAsync(CrossTenantLogAnalyticsWorkspaceOptions workspace,
            string applicationClientId, string clientSecret, TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The disabled diagnostic must not probe workspaces.");
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/admin/users");
    }

    private sealed class UiSelection : IDisposable
    {
        private readonly CultureInfo previous = CultureInfo.CurrentUICulture;
        public UiSelection(string culture) => CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        public void Dispose() => CultureInfo.CurrentUICulture = previous;
    }

    private sealed class Activator(IServiceProvider services) : IComponentActivator
    {
        public AdminUsers Users { get; private set; } = default!;
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)ActivatorUtilities.CreateInstance(services, type);
            if (component is AdminUsers users) Users = users;
            return component;
        }
    }

    private sealed class Renderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure.StaticHtmlRenderer(services, loggerFactory)
    {
        protected override IComponent ResolveComponentForRenderMode(
            Type componentType, int? parentComponentId, IComponentActivator activator, IComponentRenderMode mode) =>
            mode is InteractiveServerRenderMode ? activator.CreateInstance(componentType)
                : throw new NotSupportedException($"Unsupported test render mode: {mode.GetType().Name}");
    }

    private sealed class Fixture(SqliteConnection connection, Factory factory, WebApplication app,
        AsyncServiceScope scope, Renderer renderer) : IAsyncDisposable
    {
        public Factory Factory { get; } = factory;
        public SqlAuthenticationService Service => scope.ServiceProvider.GetRequiredService<SqlAuthenticationService>();
        public UiText Text => scope.ServiceProvider.GetRequiredService<UiText>();
        private AdminUsers Users => scope.ServiceProvider.GetRequiredService<Activator>().Users;
        private HtmlRootComponent root;
        public string Html => renderer.Dispatcher.InvokeAsync(root.ToHtmlString).GetAwaiter().GetResult();
        public string Decoded => WebUtility.HtmlDecode(Html);
        public string Error => Assert.IsType<string>(
            typeof(AdminUsers).GetField("errorMessage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Users));

        public async Task<string> Render<T>() where T : IComponent => await renderer.Dispatcher.InvokeAsync(async () =>
        {
            root = renderer.BeginRenderingComponent(typeof(T), ParameterView.Empty);
            await root.QuiescenceTask;
            return root.ToHtmlString();
        });
        public async Task RenderUsers() => _ = await Render<AdminUsers>();
        public void Set(string name, string value) =>
            typeof(AdminUsers).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Users, value);
        public T Get<T>(string name) =>
            (T)typeof(AdminUsers).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Users)!;
        public Task Invoke(string name, params object[] args) => renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = typeof(AdminUsers).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Users, args);
            if (result is Task task) await task;
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Users, null);
        });
        public async Task<SqlAuthenticationUser> ReadUser()
        {
            await using var context = Factory.CreateDbContext();
            return await context.AuthenticationUsers.AsNoTracking().SingleAsync(user => user.Username == "Original <user>");
        }
        public async Task Execute(string sql)
        {
            await using var context = Factory.CreateDbContext();
            await context.Database.ExecuteSqlRawAsync(sql);
        }
        public static async Task<Fixture> Create(string mode = "sql")
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var factory = new Factory(new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection).Options);
            await using (var context = factory.CreateDbContext())
            {
                foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries))
                    if (statement.Contains("\"AuthenticationUsers\"", StringComparison.Ordinal))
                        await context.Database.ExecuteSqlRawAsync(statement.Replace("N'", "'", StringComparison.Ordinal));
                context.AuthenticationUsers.AddRange(
                    new SqlAuthenticationUser { Username = "Original <user>", Role = "Reader", PasswordHash = new SqlPasswordHasher().Hash(Password) },
                    new SqlAuthenticationUser { Username = "administrator", Role = "Admin", PasswordHash = new SqlPasswordHasher().Hash(Password) });
                await context.SaveChangesAsync();
            }
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddRazorComponents();
            builder.Services.AddUiLocalization();
            builder.Services.AddScoped<NavigationManager, TestNavigation>();
            builder.Services.AddSingleton<IDbContextFactory<AlertDbContext>>(factory);
            builder.Services.AddSingleton<SqlPasswordHasher>();
            builder.Services.AddSingleton<SqlAuthenticationService>();
            builder.Services.AddSingleton(new ApplicationAuthenticationOptions { Type = mode });
            builder.Services.AddSingleton(new CrossTenantLogAnalyticsOptions());
            builder.Services.AddSingleton(new LogAnalyticsConnectionDiagnostics(
                new CrossTenantLogAnalyticsOptions(), new UnusedSecret(), new UnusedProbe()));
            builder.Services.AddScoped<Activator>();
            builder.Services.AddScoped<IComponentActivator>(services => services.GetRequiredService<Activator>());
            var app = builder.Build();
            var scope = app.Services.CreateAsyncScope();
            var renderer = new Renderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());
            return new Fixture(connection, factory, app, scope, renderer);
        }
        public async ValueTask DisposeAsync()
        {
            await ((IAsyncDisposable)renderer).DisposeAsync();
            await scope.DisposeAsync();
            await app.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class Factory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        public bool FailCreation { get; set; }
        public bool FailReadAfterWrite { get; set; }
        public bool ThrowAfterWrite { get; set; }
        public Exception? Failure { get; set; }
        public int CreationCount { get; private set; }
        public bool BlockNextCreation { get; set; }
        public TaskCompletionSource CreationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCreation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AlertDbContext CreateDbContext()
        {
            CreationCount++;
            if (Failure is { } failure) throw failure;
            if (FailCreation) throw new InvalidOperationException("PRIVATE_PROVIDER_DETAIL");
            return new(new DbContextOptionsBuilder<AlertDbContext>(options).AddInterceptors(new AfterWrite(this), new AfterDelete(this)).Options);
        }
        public async Task<AlertDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            if (BlockNextCreation)
            {
                BlockNextCreation = false;
                CreationStarted.SetResult();
                await ReleaseCreation.Task.WaitAsync(cancellationToken);
            }
            return CreateDbContext();
        }
    }

    private sealed class AfterWrite(Factory factory) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result,
            CancellationToken cancellationToken = default)
        {
            if (factory.FailReadAfterWrite) factory.FailCreation = true;
            if (factory.ThrowAfterWrite) throw new InvalidOperationException("PRIVATE_PROVIDER_DETAIL");
            return ValueTask.FromResult(result);
        }

    }

    private sealed class AfterDelete(Factory factory) : DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData data,
            int result, CancellationToken cancellationToken = default)
        {
            if (factory.FailReadAfterWrite && command.CommandText.StartsWith("DELETE", StringComparison.Ordinal))
                factory.FailCreation = true;
            if (factory.ThrowAfterWrite && command.CommandText.StartsWith("DELETE", StringComparison.Ordinal))
                throw new InvalidOperationException("PRIVATE_PROVIDER_DETAIL");
            return ValueTask.FromResult(result);
        }
    }
}
