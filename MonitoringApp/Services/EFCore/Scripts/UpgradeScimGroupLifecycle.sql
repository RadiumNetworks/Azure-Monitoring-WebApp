BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111050629_AddScimGroupLifecycle'
)
BEGIN
    ALTER TABLE [ScimGroups] ADD [IsDeleted] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111050629_AddScimGroupLifecycle'
)
BEGIN
    ALTER TABLE [ScimGroupMembers] ADD [IsDeleted] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111050629_AddScimGroupLifecycle'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261111050629_AddScimGroupLifecycle', N'10.0.11');
END;

COMMIT;
GO

