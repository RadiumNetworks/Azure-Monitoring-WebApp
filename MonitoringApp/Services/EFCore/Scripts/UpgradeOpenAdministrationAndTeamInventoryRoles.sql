BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111080329_AddOpenAdministrationAndTeamInventoryRoles'
)
BEGIN
    DECLARE @var nvarchar(max);
    SELECT @var = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[IdentityAdministrationRecords]') AND [c].[name] = N'ActorUserId');
    IF @var IS NOT NULL EXEC(N'ALTER TABLE [IdentityAdministrationRecords] DROP CONSTRAINT ' + @var + ';');
    ALTER TABLE [IdentityAdministrationRecords] ALTER COLUMN [ActorUserId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111080329_AddOpenAdministrationAndTeamInventoryRoles'
)
BEGIN
    ALTER TABLE [IdentityAdministrationRecords] ADD [IsOpenAuthentication] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111080329_AddOpenAdministrationAndTeamInventoryRoles'
)
BEGIN
    CREATE TABLE [UserTeamInventoryRoles] (
        [TeamId] uniqueidentifier NOT NULL,
        [RoleKey] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
        CONSTRAINT [PK_UserTeamInventoryRoles] PRIMARY KEY ([TeamId], [RoleKey]),
        CONSTRAINT [CK_UserTeamInventoryRoles_RoleKey] CHECK (LTRIM(RTRIM([RoleKey])) <> ''),
        CONSTRAINT [FK_UserTeamInventoryRoles_UserTeams_TeamId] FOREIGN KEY ([TeamId]) REFERENCES [UserTeams] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111080329_AddOpenAdministrationAndTeamInventoryRoles'
)
BEGIN
    EXEC(N'ALTER TABLE [IdentityAdministrationRecords] ADD CONSTRAINT [CK_IdentityAdministrationRecords_Actor] CHECK (([IsOpenAuthentication] = 1 AND [ActorUserId] IS NULL) OR ([IsOpenAuthentication] = 0 AND [ActorUserId] IS NOT NULL))');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111080329_AddOpenAdministrationAndTeamInventoryRoles'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261111080329_AddOpenAdministrationAndTeamInventoryRoles', N'10.0.11');
END;

COMMIT;
GO

