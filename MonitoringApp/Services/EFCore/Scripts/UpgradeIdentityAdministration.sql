BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110200132_AddIdentityAdministration'
)
BEGIN
    CREATE TABLE [IdentityAdministrationRecords] (
        [Id] uniqueidentifier NOT NULL,
        [ActorUserId] uniqueidentifier NOT NULL,
        [TargetUserId] uniqueidentifier NULL,
        [TargetTeamId] uniqueidentifier NULL,
        [Action] nvarchar(64) NOT NULL,
        [OccurredAt] datetimeoffset NOT NULL,
        [BeforeJson] nvarchar(max) NOT NULL,
        [AfterJson] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_IdentityAdministrationRecords] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_IdentityAdministrationRecords_Target] CHECK (([TargetUserId] IS NOT NULL AND [TargetTeamId] IS NULL) OR ([TargetUserId] IS NULL AND [TargetTeamId] IS NOT NULL)),
        CONSTRAINT [FK_IdentityAdministrationRecords_DirectoryUsers_ActorUserId] FOREIGN KEY ([ActorUserId]) REFERENCES [DirectoryUsers] ([Id]),
        CONSTRAINT [FK_IdentityAdministrationRecords_DirectoryUsers_TargetUserId] FOREIGN KEY ([TargetUserId]) REFERENCES [DirectoryUsers] ([Id]),
        CONSTRAINT [FK_IdentityAdministrationRecords_UserTeams_TargetTeamId] FOREIGN KEY ([TargetTeamId]) REFERENCES [UserTeams] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110200132_AddIdentityAdministration'
)
BEGIN
    CREATE INDEX [IX_IdentityAdministrationRecords_ActorUserId] ON [IdentityAdministrationRecords] ([ActorUserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110200132_AddIdentityAdministration'
)
BEGIN
    CREATE INDEX [IX_IdentityAdministrationRecords_TargetTeamId_OccurredAt] ON [IdentityAdministrationRecords] ([TargetTeamId], [OccurredAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110200132_AddIdentityAdministration'
)
BEGIN
    CREATE INDEX [IX_IdentityAdministrationRecords_TargetUserId_OccurredAt] ON [IdentityAdministrationRecords] ([TargetUserId], [OccurredAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110200132_AddIdentityAdministration'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261110200132_AddIdentityAdministration', N'10.0.11');
END;

COMMIT;
GO

