using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

internal static class IdentityModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder, bool isSqlServer)
    {
        var users = modelBuilder.Entity<DirectoryUser>();
        users.ToTable("DirectoryUsers", table =>
        {
            table.HasCheckConstraint("CK_DirectoryUsers_Id", "[Id] <> '00000000-0000-0000-0000-000000000000'");
            table.HasCheckConstraint("CK_DirectoryUsers_Origin", "[Origin] IN ('Sql', 'EntraLogin', 'Scim', 'Legacy')");
            table.HasCheckConstraint("CK_DirectoryUsers_Revision", "[Revision] > 0");
        });
        users.HasKey(user => user.Id);
        users.Property(user => user.Id).ValueGeneratedNever();
        users.Property(user => user.UserName).HasMaxLength(256);
        users.Property(user => user.DisplayName).HasMaxLength(256);
        users.Property(user => user.LocalDisplayName).HasMaxLength(256);
        users.Property(user => user.GivenName).HasMaxLength(256);
        users.Property(user => user.FamilyName).HasMaxLength(256);
        users.Property(user => user.Email).HasMaxLength(320);
        users.Property(user => user.Origin).HasMaxLength(16);
        users.Property(user => user.Revision).IsConcurrencyToken();

        var historicalAliases = modelBuilder.Entity<HistoricalUserAlias>();
        historicalAliases.ToTable("HistoricalUserAliases", table =>
            table.HasCheckConstraint("CK_HistoricalUserAliases_DistinctUsers", "[AliasUserId] <> [MainUserId]"));
        historicalAliases.HasKey(alias => alias.AliasUserId);
        historicalAliases.Property(alias => alias.AliasUserId).ValueGeneratedNever();
        historicalAliases.HasOne<DirectoryUser>().WithMany().HasForeignKey(alias => alias.AliasUserId).OnDelete(DeleteBehavior.NoAction);
        historicalAliases.HasOne<DirectoryUser>().WithMany().HasForeignKey(alias => alias.MainUserId).OnDelete(DeleteBehavior.NoAction);

        var sql = modelBuilder.Entity<SqlUserIdentity>();
        sql.ToTable("SqlUserIdentities");
        sql.HasKey(identity => identity.UserId);
        sql.Property(identity => identity.UserId).ValueGeneratedNever();
        sql.Property(identity => identity.Username).HasMaxLength(128);
        sql.HasIndex(identity => identity.Username).IsUnique();
        sql.HasOne<DirectoryUser>().WithMany().HasForeignKey(identity => identity.UserId).OnDelete(DeleteBehavior.NoAction);
        sql.HasOne<SqlAuthenticationUser>().WithMany().HasForeignKey(identity => identity.Username).OnDelete(DeleteBehavior.NoAction);

        var entra = modelBuilder.Entity<EntraUserIdentity>();
        entra.ToTable("EntraUserIdentities", table =>
        {
            table.HasCheckConstraint("CK_EntraUserIdentities_Tenant", "[TenantId] <> '00000000-0000-0000-0000-000000000000'");
            table.HasCheckConstraint("CK_EntraUserIdentities_Object", "[ObjectId] <> '00000000-0000-0000-0000-000000000000'");
        });
        entra.HasKey(identity => identity.UserId);
        entra.Property(identity => identity.UserId).ValueGeneratedNever();
        entra.HasIndex(identity => new { identity.TenantId, identity.ObjectId }).IsUnique();
        entra.HasAlternateKey(identity => new { identity.UserId, identity.TenantId, identity.ObjectId });
        entra.HasOne<DirectoryUser>().WithMany().HasForeignKey(identity => identity.UserId).OnDelete(DeleteBehavior.NoAction);

        var connections = modelBuilder.Entity<ScimConnection>();
        connections.ToTable("ScimConnections", table =>
        {
            table.HasCheckConstraint("CK_ScimConnections_Tenant", "[TenantId] <> '00000000-0000-0000-0000-000000000000'");
            table.HasCheckConstraint("CK_ScimConnections_Name", "LTRIM(RTRIM([Name])) <> ''");
            table.HasCheckConstraint("CK_ScimConnections_Revision", "[Revision] > 0");
        });
        connections.HasKey(connection => connection.Id);
        connections.Property(connection => connection.Id).ValueGeneratedNever();
        connections.Property(connection => connection.Name).HasMaxLength(256);
        connections.Property(connection => connection.Revision).IsConcurrencyToken();
        connections.HasAlternateKey(connection => new { connection.Id, connection.TenantId });

        var states = modelBuilder.Entity<ScimUserState>();
        states.ToTable("ScimUserStates", table =>
        {
            table.HasCheckConstraint("CK_ScimUserStates_Status", "[SyncStatus] IN ('Pending', 'Succeeded', 'Failed')");
            table.HasCheckConstraint("CK_ScimUserStates_Revision", "[Revision] > 0");
        });
        states.HasKey(state => state.UserId);
        states.Property(state => state.UserId).ValueGeneratedNever();
        states.Property(state => state.SyncStatus).HasMaxLength(16);
        states.Property(state => state.Revision).IsConcurrencyToken();
        states.HasAlternateKey(state => new { state.UserId, state.ConnectionId });
        states.HasIndex(state => new { state.ConnectionId, state.ExternalId }).IsUnique();
        states.HasOne<EntraUserIdentity>().WithMany()
            .HasForeignKey(state => new { state.UserId, state.TenantId, state.ExternalId })
            .HasPrincipalKey(identity => new { identity.UserId, identity.TenantId, identity.ObjectId })
            .OnDelete(DeleteBehavior.NoAction);
        states.HasOne<ScimConnection>().WithMany()
            .HasForeignKey(state => new { state.ConnectionId, state.TenantId })
            .HasPrincipalKey(connection => new { connection.Id, connection.TenantId })
            .OnDelete(DeleteBehavior.NoAction);

        var groups = modelBuilder.Entity<ScimGroup>();
        groups.ToTable("ScimGroups", table =>
        {
            table.HasCheckConstraint("CK_ScimGroups_Status", "[SyncStatus] IN ('Pending', 'Succeeded', 'Failed')");
            table.HasCheckConstraint("CK_ScimGroups_Revision", "[Revision] > 0");
            table.HasCheckConstraint("CK_ScimGroups_ExternalId", "[ExternalId] <> '00000000-0000-0000-0000-000000000000'");
        });
        groups.HasKey(group => group.Id);
        groups.Property(group => group.Id).ValueGeneratedNever();
        groups.Property(group => group.DisplayName).HasMaxLength(256);
        groups.Property(group => group.SyncStatus).HasMaxLength(16);
        groups.Property(group => group.Revision).IsConcurrencyToken();
        groups.HasAlternateKey(group => new { group.Id, group.ConnectionId });
        groups.HasIndex(group => new { group.ConnectionId, group.ExternalId }).IsUnique();
        groups.HasOne<ScimConnection>().WithMany().HasForeignKey(group => group.ConnectionId).OnDelete(DeleteBehavior.NoAction);

        var members = modelBuilder.Entity<ScimGroupMember>();
        members.ToTable("ScimGroupMembers");
        members.HasKey(member => new { member.GroupId, member.UserId });
        members.HasOne<ScimGroup>().WithMany()
            .HasForeignKey(member => new { member.GroupId, member.ConnectionId })
            .HasPrincipalKey(group => new { group.Id, group.ConnectionId })
            .OnDelete(DeleteBehavior.NoAction);
        members.HasOne<ScimUserState>().WithMany()
            .HasForeignKey(member => new { member.UserId, member.ConnectionId })
            .HasPrincipalKey(state => new { state.UserId, state.ConnectionId })
            .OnDelete(DeleteBehavior.NoAction);

        var teams = modelBuilder.Entity<UserTeam>();
        teams.ToTable("UserTeams", table =>
        {
            table.HasCheckConstraint("CK_UserTeams_Name", "LTRIM(RTRIM([Name])) <> ''");
            table.HasCheckConstraint("CK_UserTeams_Revision", "[Revision] > 0");
        });
        teams.HasKey(team => team.Id);
        teams.Property(team => team.Id).ValueGeneratedNever();
        teams.Property(team => team.Name).HasMaxLength(256);
        teams.Property(team => team.Revision).IsConcurrencyToken();
        teams.HasIndex(team => team.Name).IsUnique();

        var inventoryRoles = modelBuilder.Entity<UserTeamInventoryRole>();
        inventoryRoles.ToTable("UserTeamInventoryRoles", table =>
            table.HasCheckConstraint("CK_UserTeamInventoryRoles_RoleKey", "LTRIM(RTRIM([RoleKey])) <> ''"));
        inventoryRoles.HasKey(role => new { role.TeamId, role.RoleKey });
        inventoryRoles.Property(role => role.RoleKey).HasMaxLength(256);
        if (isSqlServer)
            inventoryRoles.Property(role => role.RoleKey).UseCollation("Latin1_General_100_BIN2");
        inventoryRoles.HasOne<UserTeam>().WithMany().HasForeignKey(role => role.TeamId).OnDelete(DeleteBehavior.NoAction);

        var mappings = modelBuilder.Entity<UserTeamGroupMapping>();
        mappings.ToTable("UserTeamGroupMappings", table =>
            table.HasCheckConstraint("CK_UserTeamGroupMappings_Revision", "[Revision] > 0"));
        mappings.HasKey(mapping => mapping.Id);
        mappings.Property(mapping => mapping.Id).ValueGeneratedNever();
        mappings.Property(mapping => mapping.Revision).IsConcurrencyToken();
        mappings.HasAlternateKey(mapping => new { mapping.Id, mapping.TeamId, mapping.GroupId });
        mappings.HasIndex(mapping => new { mapping.TeamId, mapping.GroupId }).IsUnique();
        mappings.HasOne<UserTeam>().WithMany().HasForeignKey(mapping => mapping.TeamId).OnDelete(DeleteBehavior.NoAction);
        mappings.HasOne<ScimGroup>().WithMany().HasForeignKey(mapping => mapping.GroupId).OnDelete(DeleteBehavior.NoAction);

        var memberships = modelBuilder.Entity<UserTeamMembership>();
        memberships.ToTable("UserTeamMemberships", table =>
            table.HasCheckConstraint("CK_UserTeamMemberships_Source",
                "([GroupMappingId] IS NULL AND [SourceGroupId] IS NULL) OR ([GroupMappingId] IS NOT NULL AND [SourceGroupId] IS NOT NULL)"));
        memberships.HasKey(membership => membership.Id);
        memberships.Property(membership => membership.Id).ValueGeneratedNever();
        memberships.HasIndex(membership => new { membership.UserId, membership.TeamId })
            .IsUnique().HasFilter("[GroupMappingId] IS NULL");
        memberships.HasIndex(membership => new { membership.UserId, membership.TeamId, membership.GroupMappingId })
            .IsUnique().HasFilter("[GroupMappingId] IS NOT NULL");
        memberships.HasOne<DirectoryUser>().WithMany().HasForeignKey(membership => membership.UserId).OnDelete(DeleteBehavior.NoAction);
        memberships.HasOne<UserTeam>().WithMany().HasForeignKey(membership => membership.TeamId).OnDelete(DeleteBehavior.NoAction);
        memberships.HasOne<UserTeamGroupMapping>().WithMany()
            .HasForeignKey(membership => new { membership.GroupMappingId, membership.TeamId, membership.SourceGroupId })
            .HasPrincipalKey(mapping => new { mapping.Id, mapping.TeamId, mapping.GroupId })
            .OnDelete(DeleteBehavior.NoAction);
        memberships.HasOne<ScimGroupMember>().WithMany()
            .HasForeignKey(membership => new { membership.SourceGroupId, membership.UserId })
            .OnDelete(DeleteBehavior.NoAction);

        var legacy = modelBuilder.Entity<LegacyUserLink>();
        legacy.ToTable("LegacyUserLinks", table =>
        {
            table.HasCheckConstraint("CK_LegacyUserLinks_Alias", "LTRIM(RTRIM([Alias])) <> ''");
            table.HasCheckConstraint("CK_LegacyUserLinks_Status", "[Status] IN ('Pending', 'Confirmed', 'Conflict', 'Rejected')");
            table.HasCheckConstraint("CK_LegacyUserLinks_Revision", "[Revision] > 0");
            table.HasCheckConstraint("CK_LegacyUserLinks_Approval",
                "[Status] <> 'Confirmed' OR ([TargetUserId] IS NOT NULL AND [ApprovedByUserId] IS NOT NULL AND [ApprovedAt] IS NOT NULL AND [VerificationNote] IS NOT NULL AND LTRIM(RTRIM([VerificationNote])) <> '')");
        });
        legacy.HasKey(link => link.Id);
        legacy.Property(link => link.Id).ValueGeneratedNever();
        legacy.Property(link => link.Alias).HasMaxLength(256);
        legacy.Property(link => link.Status).HasMaxLength(16);
        legacy.Property(link => link.VerificationNote).HasMaxLength(2000);
        legacy.Property(link => link.Revision).IsConcurrencyToken();
        legacy.HasIndex(link => new { link.Alias, link.TenantId }).IsUnique().HasFilter("[TenantId] IS NOT NULL");
        legacy.HasIndex(link => link.Alias).IsUnique().HasFilter("[TenantId] IS NULL");
        legacy.HasOne<DirectoryUser>().WithMany().HasForeignKey(link => link.TargetUserId).OnDelete(DeleteBehavior.NoAction);
        legacy.HasOne<DirectoryUser>().WithMany().HasForeignKey(link => link.ApprovedByUserId).OnDelete(DeleteBehavior.NoAction);

        var records = modelBuilder.Entity<IdentityMigrationRecord>();
        records.ToTable("IdentityMigrationRecords");
        records.HasKey(record => record.Id);
        records.Property(record => record.Id).ValueGeneratedNever();
        records.Property(record => record.Fingerprint).HasMaxLength(64);
        records.Property(record => record.VerificationNote).HasMaxLength(2000);
        records.Property(record => record.OriginalStateJson).HasColumnType("nvarchar(max)");
        records.HasIndex(record => new { record.UserId, record.Fingerprint }).IsUnique();
        records.HasOne<DirectoryUser>().WithMany().HasForeignKey(record => record.UserId).OnDelete(DeleteBehavior.NoAction);
        records.HasOne<DirectoryUser>().WithMany().HasForeignKey(record => record.ApprovedByUserId).OnDelete(DeleteBehavior.NoAction);

        var administration = modelBuilder.Entity<IdentityAdministrationRecord>();
        administration.ToTable("IdentityAdministrationRecords", table =>
        {
            table.HasCheckConstraint("CK_IdentityAdministrationRecords_Target",
                "([TargetUserId] IS NOT NULL AND [TargetTeamId] IS NULL) OR ([TargetUserId] IS NULL AND [TargetTeamId] IS NOT NULL)");
            table.HasCheckConstraint("CK_IdentityAdministrationRecords_Actor",
                "([IsOpenAuthentication] = 1 AND [ActorUserId] IS NULL) OR ([IsOpenAuthentication] = 0 AND [ActorUserId] IS NOT NULL)");
        });
        administration.HasKey(record => record.Id);
        administration.Property(record => record.Id).ValueGeneratedNever();
        administration.Property(record => record.IsOpenAuthentication).HasDefaultValue(false);
        administration.Property(record => record.Action).HasMaxLength(64);
        administration.Property(record => record.BeforeJson).HasColumnType("nvarchar(max)");
        administration.Property(record => record.AfterJson).HasColumnType("nvarchar(max)");
        administration.HasIndex(record => new { record.TargetUserId, record.OccurredAt });
        administration.HasIndex(record => new { record.TargetTeamId, record.OccurredAt });
        administration.HasOne<DirectoryUser>().WithMany().HasForeignKey(record => record.ActorUserId).OnDelete(DeleteBehavior.NoAction);
        administration.HasOne<DirectoryUser>().WithMany().HasForeignKey(record => record.TargetUserId).OnDelete(DeleteBehavior.NoAction);
        administration.HasOne<UserTeam>().WithMany().HasForeignKey(record => record.TargetTeamId).OnDelete(DeleteBehavior.NoAction);
    }
}
