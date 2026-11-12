SET QUOTED_IDENTIFIER ON;
GO
BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111120000_RetireReplicationInventoryWarning'
)
BEGIN
    UPDATE [AlertRules] SET [Enabled] = 0 WHERE [RuleType] = N'ReplicationInventoryWarning';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111120000_RetireReplicationInventoryWarning'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261111120000_RetireReplicationInventoryWarning', N'10.0.11');
END;

COMMIT;
GO
