BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111131740_AddHistoricalUserAliases'
)
BEGIN
    CREATE TABLE [HistoricalUserAliases] (
        [AliasUserId] uniqueidentifier NOT NULL,
        [MainUserId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_HistoricalUserAliases] PRIMARY KEY ([AliasUserId]),
        CONSTRAINT [CK_HistoricalUserAliases_DistinctUsers] CHECK ([AliasUserId] <> [MainUserId]),
        CONSTRAINT [FK_HistoricalUserAliases_DirectoryUsers_AliasUserId] FOREIGN KEY ([AliasUserId]) REFERENCES [DirectoryUsers] ([Id]),
        CONSTRAINT [FK_HistoricalUserAliases_DirectoryUsers_MainUserId] FOREIGN KEY ([MainUserId]) REFERENCES [DirectoryUsers] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111131740_AddHistoricalUserAliases'
)
BEGIN
    CREATE INDEX [IX_HistoricalUserAliases_MainUserId] ON [HistoricalUserAliases] ([MainUserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111131740_AddHistoricalUserAliases'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261111131740_AddHistoricalUserAliases', N'10.0.11');
END;

COMMIT;
GO

