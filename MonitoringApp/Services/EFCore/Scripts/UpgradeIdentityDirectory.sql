BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    CREATE TABLE [DirectoryUsers] (
        [Id] uniqueidentifier NOT NULL,
        [UserName] nvarchar(256) NOT NULL,
        [DisplayName] nvarchar(256) NOT NULL,
        [LocalDisplayName] nvarchar(256) NULL,
        [GivenName] nvarchar(256) NULL,
        [FamilyName] nvarchar(256) NULL,
        [Email] nvarchar(320) NULL,
        [Origin] nvarchar(16) NOT NULL,
        [IsEnabled] bit NOT NULL,
        [Revision] int NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_DirectoryUsers] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_DirectoryUsers_Id] CHECK ([Id] <> '00000000-0000-0000-0000-000000000000'),
        CONSTRAINT [CK_DirectoryUsers_Origin] CHECK ([Origin] IN ('Sql', 'EntraLogin', 'Scim', 'Legacy')),
        CONSTRAINT [CK_DirectoryUsers_Revision] CHECK ([Revision] > 0)
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    CREATE TABLE [ScimConnections] (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(256) NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [IsEnabled] bit NOT NULL,
        [Revision] int NOT NULL,
        CONSTRAINT [PK_ScimConnections] PRIMARY KEY ([Id]),
        CONSTRAINT [AK_ScimConnections_Id_TenantId] UNIQUE ([Id], [TenantId]),
        CONSTRAINT [CK_ScimConnections_Name] CHECK (LTRIM(RTRIM([Name])) <> ''),
        CONSTRAINT [CK_ScimConnections_Revision] CHECK ([Revision] > 0),
        CONSTRAINT [CK_ScimConnections_Tenant] CHECK ([TenantId] <> '00000000-0000-0000-0000-000000000000')
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    CREATE TABLE [UserTeams] (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(256) NOT NULL,
        [IsEnabled] bit NOT NULL,
        [Revision] int NOT NULL,
        CONSTRAINT [PK_UserTeams] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_UserTeams_Name] CHECK (LTRIM(RTRIM([Name])) <> ''),
        CONSTRAINT [CK_UserTeams_Revision] CHECK ([Revision] > 0)
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    CREATE TABLE [EntraUserIdentities] (
        [UserId] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [ObjectId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_EntraUserIdentities] PRIMARY KEY ([UserId]),
        CONSTRAINT [AK_EntraUserIdentities_UserId_TenantId_ObjectId] UNIQUE ([UserId], [TenantId], [ObjectId]),
        CONSTRAINT [CK_EntraUserIdentities_Object] CHECK ([ObjectId] <> '00000000-0000-0000-0000-000000000000'),
        CONSTRAINT [CK_EntraUserIdentities_Tenant] CHECK ([TenantId] <> '00000000-0000-0000-0000-000000000000'),
        CONSTRAINT [FK_EntraUserIdentities_DirectoryUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [DirectoryUsers] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    CREATE TABLE [LegacyUserLinks] (
        [Id] uniqueidentifier NOT NULL,
        [Alias] nvarchar(256) NOT NULL,
        [TenantId] uniqueidentifier NULL,
        [TargetUserId] uniqueidentifier NULL,
        [Status] nvarchar(16) NOT NULL,
        [ApprovedByUserId] uniqueidentifier NULL,
        [ApprovedAt] datetimeoffset NULL,
        [VerificationNote] nvarchar(2000) NULL,
        [Revision] int NOT NULL,
        CONSTRAINT [PK_LegacyUserLinks] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_LegacyUserLinks_Alias] CHECK (LTRIM(RTRIM([Alias])) <> ''),
        CONSTRAINT [CK_LegacyUserLinks_Approval] CHECK ([Status] <> 'Confirmed' OR ([TargetUserId] IS NOT NULL AND [ApprovedByUserId] IS NOT NULL AND [ApprovedAt] IS NOT NULL AND [VerificationNote] IS NOT NULL AND LTRIM(RTRIM([VerificationNote])) <> '')),
        CONSTRAINT [CK_LegacyUserLinks_Revision] CHECK ([Revision] > 0),
        CONSTRAINT [CK_LegacyUserLinks_Status] CHECK ([Status] IN ('Pending', 'Confirmed', 'Conflict', 'Rejected')),
        CONSTRAINT [FK_LegacyUserLinks_DirectoryUsers_ApprovedByUserId] FOREIGN KEY ([ApprovedByUserId]) REFERENCES [DirectoryUsers] ([Id]),
        CONSTRAINT [FK_LegacyUserLinks_DirectoryUsers_TargetUserId] FOREIGN KEY ([TargetUserId]) REFERENCES [DirectoryUsers] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    CREATE TABLE [SqlUserIdentities] (
        [UserId] uniqueidentifier NOT NULL,
        [Username] nvarchar(128) NOT NULL,
        CONSTRAINT [PK_SqlUserIdentities] PRIMARY KEY ([UserId]),
        CONSTRAINT [FK_SqlUserIdentities_AuthenticationUsers_Username] FOREIGN KEY ([Username]) REFERENCES [AuthenticationUsers] ([Username]),
        CONSTRAINT [FK_SqlUserIdentities_DirectoryUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [DirectoryUsers] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    CREATE TABLE [ScimGroups] (
        [Id] uniqueidentifier NOT NULL,
        [ConnectionId] uniqueidentifier NOT NULL,
        [ExternalId] uniqueidentifier NOT NULL,
        [DisplayName] nvarchar(256) NOT NULL,
        [IsActive] bit NOT NULL,
        [SyncStatus] nvarchar(16) NOT NULL,
        [LastSyncedAt] datetimeoffset NULL,
        [Revision] int NOT NULL,
        CONSTRAINT [PK_ScimGroups] PRIMARY KEY ([Id]),
        CONSTRAINT [AK_ScimGroups_Id_ConnectionId] UNIQUE ([Id], [ConnectionId]),
        CONSTRAINT [CK_ScimGroups_ExternalId] CHECK ([ExternalId] <> '00000000-0000-0000-0000-000000000000'),
        CONSTRAINT [CK_ScimGroups_Revision] CHECK ([Revision] > 0),
        CONSTRAINT [CK_ScimGroups_Status] CHECK ([SyncStatus] IN ('Pending', 'Succeeded', 'Failed')),
        CONSTRAINT [FK_ScimGroups_ScimConnections_ConnectionId] FOREIGN KEY ([ConnectionId]) REFERENCES [ScimConnections] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    CREATE TABLE [ScimUserStates] (
        [UserId] uniqueidentifier NOT NULL,
        [ConnectionId] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [ExternalId] uniqueidentifier NOT NULL,
        [IsActive] bit NOT NULL,
        [SyncStatus] nvarchar(16) NOT NULL,
        [LastSyncedAt] datetimeoffset NULL,
        [Revision] int NOT NULL,
        CONSTRAINT [PK_ScimUserStates] PRIMARY KEY ([UserId]),
        CONSTRAINT [AK_ScimUserStates_UserId_ConnectionId] UNIQUE ([UserId], [ConnectionId]),
        CONSTRAINT [CK_ScimUserStates_Revision] CHECK ([Revision] > 0),
        CONSTRAINT [CK_ScimUserStates_Status] CHECK ([SyncStatus] IN ('Pending', 'Succeeded', 'Failed')),
        CONSTRAINT [FK_ScimUserStates_EntraUserIdentities_UserId_TenantId_ExternalId] FOREIGN KEY ([UserId], [TenantId], [ExternalId]) REFERENCES [EntraUserIdentities] ([UserId], [TenantId], [ObjectId]),
        CONSTRAINT [FK_ScimUserStates_ScimConnections_ConnectionId_TenantId] FOREIGN KEY ([ConnectionId], [TenantId]) REFERENCES [ScimConnections] ([Id], [TenantId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    CREATE TABLE [UserTeamGroupMappings] (
        [Id] uniqueidentifier NOT NULL,
        [TeamId] uniqueidentifier NOT NULL,
        [GroupId] uniqueidentifier NOT NULL,
        [IsEnabled] bit NOT NULL,
        [Revision] int NOT NULL,
        CONSTRAINT [PK_UserTeamGroupMappings] PRIMARY KEY ([Id]),
        CONSTRAINT [AK_UserTeamGroupMappings_Id_TeamId_GroupId] UNIQUE ([Id], [TeamId], [GroupId]),
        CONSTRAINT [CK_UserTeamGroupMappings_Revision] CHECK ([Revision] > 0),
        CONSTRAINT [FK_UserTeamGroupMappings_ScimGroups_GroupId] FOREIGN KEY ([GroupId]) REFERENCES [ScimGroups] ([Id]),
        CONSTRAINT [FK_UserTeamGroupMappings_UserTeams_TeamId] FOREIGN KEY ([TeamId]) REFERENCES [UserTeams] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    CREATE TABLE [ScimGroupMembers] (
        [GroupId] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [ConnectionId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_ScimGroupMembers] PRIMARY KEY ([GroupId], [UserId]),
        CONSTRAINT [FK_ScimGroupMembers_ScimGroups_GroupId_ConnectionId] FOREIGN KEY ([GroupId], [ConnectionId]) REFERENCES [ScimGroups] ([Id], [ConnectionId]),
        CONSTRAINT [FK_ScimGroupMembers_ScimUserStates_UserId_ConnectionId] FOREIGN KEY ([UserId], [ConnectionId]) REFERENCES [ScimUserStates] ([UserId], [ConnectionId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    CREATE TABLE [UserTeamMemberships] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [TeamId] uniqueidentifier NOT NULL,
        [GroupMappingId] uniqueidentifier NULL,
        [SourceGroupId] uniqueidentifier NULL,
        CONSTRAINT [PK_UserTeamMemberships] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_UserTeamMemberships_Source] CHECK (([GroupMappingId] IS NULL AND [SourceGroupId] IS NULL) OR ([GroupMappingId] IS NOT NULL AND [SourceGroupId] IS NOT NULL)),
        CONSTRAINT [FK_UserTeamMemberships_DirectoryUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [DirectoryUsers] ([Id]),
        CONSTRAINT [FK_UserTeamMemberships_ScimGroupMembers_SourceGroupId_UserId] FOREIGN KEY ([SourceGroupId], [UserId]) REFERENCES [ScimGroupMembers] ([GroupId], [UserId]),
        CONSTRAINT [FK_UserTeamMemberships_UserTeamGroupMappings_GroupMappingId_TeamId_SourceGroupId] FOREIGN KEY ([GroupMappingId], [TeamId], [SourceGroupId]) REFERENCES [UserTeamGroupMappings] ([Id], [TeamId], [GroupId]),
        CONSTRAINT [FK_UserTeamMemberships_UserTeams_TeamId] FOREIGN KEY ([TeamId]) REFERENCES [UserTeams] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    CREATE UNIQUE INDEX [IX_EntraUserIdentities_TenantId_ObjectId] ON [EntraUserIdentities] ([TenantId], [ObjectId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_LegacyUserLinks_Alias] ON [LegacyUserLinks] ([Alias]) WHERE [TenantId] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_LegacyUserLinks_Alias_TenantId] ON [LegacyUserLinks] ([Alias], [TenantId]) WHERE [TenantId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    CREATE INDEX [IX_LegacyUserLinks_ApprovedByUserId] ON [LegacyUserLinks] ([ApprovedByUserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    CREATE INDEX [IX_LegacyUserLinks_TargetUserId] ON [LegacyUserLinks] ([TargetUserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    CREATE INDEX [IX_ScimGroupMembers_GroupId_ConnectionId] ON [ScimGroupMembers] ([GroupId], [ConnectionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    CREATE INDEX [IX_ScimGroupMembers_UserId_ConnectionId] ON [ScimGroupMembers] ([UserId], [ConnectionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ScimGroups_ConnectionId_ExternalId] ON [ScimGroups] ([ConnectionId], [ExternalId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ScimUserStates_ConnectionId_ExternalId] ON [ScimUserStates] ([ConnectionId], [ExternalId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    CREATE INDEX [IX_ScimUserStates_ConnectionId_TenantId] ON [ScimUserStates] ([ConnectionId], [TenantId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    CREATE INDEX [IX_ScimUserStates_UserId_TenantId_ExternalId] ON [ScimUserStates] ([UserId], [TenantId], [ExternalId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    CREATE UNIQUE INDEX [IX_SqlUserIdentities_Username] ON [SqlUserIdentities] ([Username]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    CREATE INDEX [IX_UserTeamGroupMappings_GroupId] ON [UserTeamGroupMappings] ([GroupId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    CREATE UNIQUE INDEX [IX_UserTeamGroupMappings_TeamId_GroupId] ON [UserTeamGroupMappings] ([TeamId], [GroupId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    CREATE INDEX [IX_UserTeamMemberships_GroupMappingId_TeamId_SourceGroupId] ON [UserTeamMemberships] ([GroupMappingId], [TeamId], [SourceGroupId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    CREATE INDEX [IX_UserTeamMemberships_SourceGroupId_UserId] ON [UserTeamMemberships] ([SourceGroupId], [UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    CREATE INDEX [IX_UserTeamMemberships_TeamId] ON [UserTeamMemberships] ([TeamId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_UserTeamMemberships_UserId_TeamId] ON [UserTeamMemberships] ([UserId], [TeamId]) WHERE [GroupMappingId] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_UserTeamMemberships_UserId_TeamId_GroupMappingId] ON [UserTeamMemberships] ([UserId], [TeamId], [GroupMappingId]) WHERE [GroupMappingId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    CREATE UNIQUE INDEX [IX_UserTeams_Name] ON [UserTeams] ([Name]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110112945_AddIdentityDirectory'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261110112945_AddIdentityDirectory', N'10.0.11');
END;

COMMIT;
GO

