BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110184457_AddIdentityActivation'
)
BEGIN
    ALTER TABLE [DirectoryUsers] ADD [UsesStableIdentity] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110184457_AddIdentityActivation'
)
BEGIN
    CREATE TABLE [IdentityMigrationRecords] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [ApprovedByUserId] uniqueidentifier NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [Fingerprint] nvarchar(64) NOT NULL,
        [VerificationNote] nvarchar(2000) NOT NULL,
        [OriginalStateJson] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_IdentityMigrationRecords] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_IdentityMigrationRecords_DirectoryUsers_ApprovedByUserId] FOREIGN KEY ([ApprovedByUserId]) REFERENCES [DirectoryUsers] ([Id]),
        CONSTRAINT [FK_IdentityMigrationRecords_DirectoryUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [DirectoryUsers] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110184457_AddIdentityActivation'
)
BEGIN
    CREATE INDEX [IX_IdentityMigrationRecords_ApprovedByUserId] ON [IdentityMigrationRecords] ([ApprovedByUserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110184457_AddIdentityActivation'
)
BEGIN
    CREATE UNIQUE INDEX [IX_IdentityMigrationRecords_UserId_Fingerprint] ON [IdentityMigrationRecords] ([UserId], [Fingerprint]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110184457_AddIdentityActivation'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261110184457_AddIdentityActivation', N'10.0.11');
END;

COMMIT;
GO

