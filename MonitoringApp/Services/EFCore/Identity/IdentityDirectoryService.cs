using System.Security.Claims;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

public sealed class IdentityDirectoryService(
    IDbContextFactory<AlertDbContext> factory,
    IdentityDirectoryOptions options,
    ApplicationAuthenticationOptions authentication)
{
    public bool Enabled => options.Enabled;

    public async Task<ClaimsPrincipal> ResolveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        var result = new ClaimsPrincipal(principal.Identities
            .Where(identity => identity.AuthenticationType != DirectoryIdentity.AuthenticationType)
            .Select(identity => new ClaimsIdentity(identity)));
        if (!Enabled || authentication.IsOpen)
            return result;
        var scheme = authentication.IsEntra ? AppServiceAuthenticationHandler.SchemeName : ApplicationAuthorization.SqlCookieScheme;
        if (!result.Identities.Any(identity => identity.IsAuthenticated && identity.AuthenticationType == scheme) ||
            !SqlAuthenticationRoles.All.Any(result.IsInRole))
            throw new UnauthorizedAccessException("Application access is required to resolve a directory identity.");

        var account = await ResolveAccountAsync(result, cancellationToken);
        var claims = new List<Claim>
        {
            new(DirectoryIdentity.UserIdClaim, account.Id.ToString("D"), ClaimValueTypes.String, DirectoryIdentity.Issuer)
        };
        if (account.UsesStableIdentity)
        {
            claims.Add(new(DirectoryIdentity.ActorClaim, DirectoryIdentity.OwnerKey(account.Id), ClaimValueTypes.String, DirectoryIdentity.Issuer));
            await using var context = await factory.CreateDbContextAsync(cancellationToken);
            var aliases = await context.LegacyUserLinks.AsNoTracking()
                .Where(link => link.TargetUserId == account.Id && link.Status == LegacyUserLinkStatuses.Confirmed)
                .Select(link => link.Alias).ToArrayAsync(cancellationToken);
            claims.AddRange(aliases.Select(alias =>
                new Claim(DirectoryIdentity.JobAliasClaim, alias, ClaimValueTypes.String, DirectoryIdentity.Issuer)));
        }
        else
        {
            // Reserved aliases must not become the private library of a renamed/reused login.
            var name = new CurrentUserIdentity(new Microsoft.AspNetCore.Http.HttpContextAccessor()).GetDisplayName(result);
            var jobActor = AgentAnalysisJobApi.GetActor(result, false);
            await using var context = await factory.CreateDbContextAsync(cancellationToken);
            if (await context.LegacyUserLinks.AnyAsync(link =>
                link.Status == LegacyUserLinkStatuses.Confirmed && (link.Alias == name || link.Alias == jobActor), cancellationToken))
                throw new UnauthorizedAccessException("This historical identity is already linked. An administrator must review the account.");
        }
        result.AddIdentity(new ClaimsIdentity(claims, DirectoryIdentity.AuthenticationType));
        return result;
    }

    private async Task<DirectoryUser> ResolveAccountAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        Guid tenantId = default, objectId = default;
        string? sqlUsername = null;
        if (authentication.IsEntra)
        {
            tenantId = ReadGuid(principal, "tid", "http://schemas.microsoft.com/identity/claims/tenantid");
            objectId = ReadGuid(principal, "oid", "http://schemas.microsoft.com/identity/claims/objectidentifier");
            if (principal.HasClaim("idtyp", "app"))
                throw new UnauthorizedAccessException("A user identity is required.");
        }
        else
        {
            sqlUsername = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? throw new UnauthorizedAccessException("The SQL identity has no identifier.");
        }

        async Task<DirectoryUser> WriteAsync()
        {
            await using var context = await factory.CreateDbContextAsync(cancellationToken);
            DirectoryUser? user;
            if (authentication.IsEntra)
                user = await (from identity in context.EntraUserIdentities
                              join entry in context.DirectoryUsers on identity.UserId equals entry.Id
                              where identity.TenantId == tenantId && identity.ObjectId == objectId
                              select entry).SingleOrDefaultAsync(cancellationToken);
            else
            {
                var sqlRole = await context.AuthenticationUsers.Where(entry => entry.Username == sqlUsername)
                    .Select(entry => entry.Role).SingleOrDefaultAsync(cancellationToken);
                if (sqlRole is null || !principal.IsInRole(sqlRole))
                    throw new UnauthorizedAccessException("The SQL account no longer exists.");
                user = await (from identity in context.SqlUserIdentities
                              join entry in context.DirectoryUsers on identity.UserId equals entry.Id
                              where identity.Username == sqlUsername
                              select entry).SingleOrDefaultAsync(cancellationToken);
            }

            if (user is null)
            {
                user = new DirectoryUser
                {
                    Origin = authentication.IsEntra ? DirectoryUserOrigins.EntraLogin : DirectoryUserOrigins.Sql,
                    UserName = sqlUsername ?? Profile(principal, "preferred_username", ClaimTypes.Upn) ?? "",
                    DisplayName = Profile(principal, "name", ClaimTypes.Name) ?? "",
                    GivenName = Profile(principal, "given_name", ClaimTypes.GivenName),
                    FamilyName = Profile(principal, "family_name", ClaimTypes.Surname),
                    Email = Profile(principal, "email", ClaimTypes.Email)
                };
                context.DirectoryUsers.Add(user);
                if (authentication.IsEntra)
                    context.EntraUserIdentities.Add(new() { UserId = user.Id, TenantId = tenantId, ObjectId = objectId });
                else
                    context.SqlUserIdentities.Add(new() { UserId = user.Id, Username = sqlUsername! });
                await context.SaveChangesAsync(cancellationToken);
            }
            else if (authentication.IsEntra && !await context.ScimUserStates.AnyAsync(state => state.UserId == user.Id, cancellationToken))
            {
                var name = Profile(principal, "preferred_username", ClaimTypes.Upn) ?? "";
                var displayName = Profile(principal, "name", ClaimTypes.Name) ?? "";
                var given = Profile(principal, "given_name", ClaimTypes.GivenName);
                var family = Profile(principal, "family_name", ClaimTypes.Surname);
                var email = Profile(principal, "email", ClaimTypes.Email);
                if ((user.UserName, user.DisplayName, user.GivenName, user.FamilyName, user.Email) !=
                    (name, displayName, given, family, email))
                {
                    user.UserName = name;
                    user.DisplayName = displayName;
                    user.GivenName = given;
                    user.FamilyName = family;
                    user.Email = email;
                    user.Revision++;
                    user.UpdatedAt = DateTimeOffset.UtcNow;
                    await context.SaveChangesAsync(cancellationToken);
                }
            }
            if (!user.IsEnabled || await context.ScimUserStates.AnyAsync(state => state.UserId == user.Id && !state.IsActive, cancellationToken))
                throw new UnauthorizedAccessException("This application identity is disabled.");
            return user;
        }

        try { return await WriteAsync(); }
        catch (DbUpdateException exception) when (IsDuplicate(exception))
        {
            // Only a concurrent identity insert is recoverable; verify it actually exists.
            await using var context = await factory.CreateDbContextAsync(cancellationToken);
            var exists = authentication.IsEntra
                ? await context.EntraUserIdentities.AnyAsync(entry => entry.TenantId == tenantId && entry.ObjectId == objectId, cancellationToken)
                : await context.SqlUserIdentities.AnyAsync(entry => entry.Username == sqlUsername, cancellationToken);
            if (!exists) throw;
            return await WriteAsync();
        }
    }

    public async Task<IReadOnlyList<DirectoryAccount>> ListAsync(ClaimsPrincipal administrator, CancellationToken cancellationToken = default)
    {
        await RequireAdministratorAsync(administrator);
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        return await (from user in context.DirectoryUsers.AsNoTracking()
                      join external in context.EntraUserIdentities on user.Id equals external.UserId into identities
                      from external in identities.DefaultIfEmpty()
                      where external != null || context.SqlUserIdentities.Any(identity => identity.UserId == user.Id)
                      orderby user.UserName, user.Id
                      select new DirectoryAccount(user.Id, user.UserName, user.LocalDisplayName ?? user.DisplayName,
                          external == null ? "SQL" : "Entra", external == null ? null : external.TenantId,
                          external == null ? null : external.ObjectId, user.IsEnabled, user.UsesStableIdentity))
            .ToArrayAsync(cancellationToken);
    }

    internal async Task<Guid> RequireAdministratorAsync(ClaimsPrincipal administrator, CancellationToken cancellationToken = default)
    {
        if (!Enabled || authentication.IsOpen || !administrator.IsInRole(SqlAuthenticationRoles.Admin))
            throw new UnauthorizedAccessException("Identity administration requires enabled directory mode and an authenticated administrator.");
        var resolved = await ResolveAsync(administrator, cancellationToken);
        return DirectoryIdentity.UserId(resolved)
            ?? throw new UnauthorizedAccessException("The administrator has no directory identity.");
    }

    private static Guid ReadGuid(ClaimsPrincipal principal, params string[] types)
    {
        var values = types.SelectMany(type => principal.FindAll(type)).Select(claim => claim.Value)
            .Select(value => Guid.TryParse(value, out var id) && id != Guid.Empty
                ? id : throw new UnauthorizedAccessException("A stable Entra tenant and object identifier are required."))
            .Distinct().ToArray();
        return values.Length == 1 ? values[0]
            : throw new UnauthorizedAccessException("A unique stable Entra tenant and object identifier are required.");
    }

    private static string? Profile(ClaimsPrincipal principal, params string[] types)
    {
        var value = types.Select(principal.FindFirstValue).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
        if (value?.Length > (types.Contains("email") ? 320 : 256))
            throw new InvalidOperationException("An identity profile attribute exceeds its supported length.");
        return value;
    }

    private static bool IsDuplicate(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };
}
