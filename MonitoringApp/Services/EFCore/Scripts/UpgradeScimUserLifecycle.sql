BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110192113_AddScimUserLifecycle'
)
BEGIN
    ALTER TABLE [ScimUserStates] ADD [IsDeleted] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110192113_AddScimUserLifecycle'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261110192113_AddScimUserLifecycle', N'10.0.11');
END;

COMMIT;
GO

