using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace MonitoringApp.Tests;

public sealed class ScimPersistenceTests
{
    [Fact]
    public async Task DifferentTenantsStaySeparateAndAnAuthoritativeSourceCannotBeTakenOver()
    {
        await using var host = await ScimTestHost.CreateAsync();
        await using var scope = host.App.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProvisioningUserStore>();
        var firstSource = host.Options.Source;
        var otherTenant = new ProvisioningSource(Guid.NewGuid(), Guid.NewGuid(), "Other tenant");
        var otherConnection = new ProvisioningSource(Guid.NewGuid(), firstSource.TenantId, "Other source");
        foreach (var source in new[] { firstSource, otherTenant, otherConnection })
            await store.EnsureSourceAsync(source, default);
        var profile = ScimUserProtocol.Read(ScimProtocolTests.User());
        var first = await store.CreateAsync(firstSource, profile, default);
        var second = await store.CreateAsync(otherTenant, profile, default);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(ProvisioningError.NotFound, (await Assert.ThrowsAsync<ProvisioningException>(() =>
            store.GetAsync(otherTenant, first.Id, default))).Code);
        Assert.Equal(ProvisioningError.Conflict, (await Assert.ThrowsAsync<ProvisioningException>(() =>
            store.CreateAsync(otherConnection, profile, default))).Code);
        await store.DeleteAsync(firstSource, first.Id, null, default);
        Assert.Equal(ProvisioningError.Conflict, (await Assert.ThrowsAsync<ProvisioningException>(() =>
            store.CreateAsync(otherConnection, profile, default))).Code);
        await using var context = host.CreateDbContext();
        Assert.Equal(2, await context.DirectoryUsers.CountAsync());
        Assert.Equal(2, await context.EntraUserIdentities.CountAsync());
    }

    [Fact]
    public async Task SqlCredentialsAndDirectoryIdentityAreNeverMergedByMatchingUsername()
    {
        await using var host = await ScimTestHost.CreateAsync();
        Guid sqlUserId;
        await using (var context = host.CreateDbContext())
        {
            var user = new DirectoryUser { Origin = DirectoryUserOrigins.Sql, UserName = "same@contoso.test" };
            sqlUserId = user.Id;
            context.DirectoryUsers.Add(user);
            context.AuthenticationUsers.Add(new() { Username = user.UserName, PasswordHash = "synthetic-unchanged", Role = "Admin" });
            context.SqlUserIdentities.Add(new() { UserId = user.Id, Username = user.UserName });
            await context.SaveChangesAsync();
        }
        await using var scope = host.App.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProvisioningUserStore>();
        await store.EnsureSourceAsync(host.Options.Source, default);
        var created = await store.CreateAsync(host.Options.Source,
            ScimUserProtocol.Read(ScimProtocolTests.User(userName: "same@contoso.test")), default);
        Assert.NotEqual(sqlUserId, created.Id);
        await using var verify = host.CreateDbContext();
        Assert.Equal(2, await verify.DirectoryUsers.CountAsync());
        Assert.Equal("synthetic-unchanged", (await verify.AuthenticationUsers.SingleAsync()).PasswordHash);
        Assert.Equal("Admin", (await verify.AuthenticationUsers.SingleAsync()).Role);
        Assert.Equal(sqlUserId, (await verify.SqlUserIdentities.SingleAsync()).UserId);
    }

    [Fact]
    public async Task ProvisioningDeactivationAndDeletionInvalidateAnAlreadyKnownPrincipal()
    {
        await using var host = await ScimTestHost.CreateAsync();
        await using var scope = host.App.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProvisioningUserStore>();
        await store.EnsureSourceAsync(host.Options.Source, default);
        var created = await store.CreateAsync(host.Options.Source, ScimUserProtocol.Read(ScimProtocolTests.User()), default);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new("tid", host.Options.TenantId.ToString()), new("oid", created.Profile.ExternalId.ToString()),
            new("preferred_username", created.Profile.UserName), new(ClaimTypes.Role, "Operator"),
            new(ClaimTypes.NameIdentifier, created.Profile.ExternalId.ToString())
        ], AppServiceAuthenticationHandler.SchemeName));
        var directory = new IdentityDirectoryService(host, new() { Enabled = true }, new() { Type = "entra" });
        var resolved = await directory.ResolveAsync(principal);
        Assert.Equal(created.Id, DirectoryIdentity.UserId(resolved));
        var inactive = await store.ReplaceAsync(host.Options.Source, created.Id,
            created.Profile with { Active = false }, created.Version, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => directory.ResolveAsync(resolved));
        await store.ReplaceAsync(host.Options.Source, created.Id, created.Profile, inactive.Version, default);
        await directory.ResolveAsync(resolved);
        await store.DeleteAsync(host.Options.Source, created.Id, null, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => directory.ResolveAsync(resolved));
        await using var context = host.CreateDbContext();
        var user = await context.DirectoryUsers.SingleAsync();
        Assert.True(user.IsEnabled);
        Assert.False(user.UsesStableIdentity);
        Assert.True((await context.ScimUserStates.SingleAsync()).IsDeleted);
    }

    [Fact]
    public void AdditiveUpgradeIsGeneratedExactlyEmbeddedAndMatchesTheCurrentSnapshot()
    {
        using var context = new AlertDbContext(new DbContextOptionsBuilder<AlertDbContext>()
            .UseSqlServer("Server=unused;Database=ScimSchemaOnly;Integrated Security=true;TrustServerCertificate=true").Options);
        Assert.False(context.Database.HasPendingModelChanges());
        var generated = context.GetService<IMigrator>().GenerateScript(
            "20261110184457_AddIdentityActivation", "20261110192113_AddScimUserLifecycle",
            MigrationsSqlGenerationOptions.Idempotent);
        var scripts = Path.Combine(ProjectDirectory(), "Services", "EFCore", "Scripts");
        var packaged = File.ReadAllText(Path.Combine(scripts, "UpgradeScimUserLifecycle.sql"));
        Assert.Equal(generated.Replace("\r\n", "\n").Trim(), packaged.Replace("\r\n", "\n").Trim());
        Assert.Contains(packaged.Replace("\r\n", "\n").Trim(),
            File.ReadAllText(Path.Combine(scripts, "CreateMonitoringDatabase.template.sql")).Replace("\r\n", "\n"));
        Assert.Contains("DEFAULT CAST(0 AS bit)", generated);
    }

    private static string ProjectDirectory([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "MonitoringApp"));
}
