/*
    MonitoringApp - Azure SQL setup template

    Replace <database-name> in both sections with the Azure SQL database name.

    This file contains two sections because Azure SQL Database does not support
    switching from master to a user database with USE.

    1. Run SECTION 1 while connected to the master database.
    2. Open a new connection to the configured database.
    3. Run SECTION 2 in that connection.

    The script is idempotent and can be executed again safely.
*/

/* ========================================================================== */
/* SECTION 1: Run against the master database                                 */
/* ========================================================================== */

DECLARE @DatabaseName sysname = N'<database-name>';

IF @DatabaseName = N'<database-name>'
BEGIN
    THROW 50000, 'Replace <database-name> before executing this script.', 1;
END;

IF DB_ID(@DatabaseName) IS NULL
BEGIN
    EXEC(N'CREATE DATABASE ' + QUOTENAME(@DatabaseName) + N';');
END;
GO

/*
    STOP HERE.

    Reconnect to the configured database before executing SECTION 2.
*/

/* ========================================================================== */
/* SECTION 2: Run against the configured database                             */
/* ========================================================================== */

DECLARE @DatabaseName sysname = N'<database-name>';

IF @DatabaseName = N'<database-name>'
BEGIN
    THROW 50000, 'Replace <database-name> before executing this script.', 1;
END;

IF DB_NAME() <> @DatabaseName
BEGIN
    THROW 50001, 'SECTION 2 must be executed in the configured database.', 1;
END;
GO

IF OBJECT_ID(N'[dbo].[Alerts]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[Alerts]
    (
        [Id]               uniqueidentifier NOT NULL,
        [ReceivedAt]       datetimeoffset    NOT NULL,
        [AlertId]          nvarchar(512)     NOT NULL,
        [Name]             nvarchar(512)     NOT NULL,
        [Severity]         nvarchar(64)      NOT NULL,
        [Status]           nvarchar(64)      NOT NULL,
        [SignalType]       nvarchar(128)     NOT NULL,
        [MonitorCondition] nvarchar(64)      NOT NULL,
        [Target]           nvarchar(2048)    NOT NULL,
        [ResourceGroup]    nvarchar(256)     NOT NULL,
        [SubscriptionId]   nvarchar(64)      NOT NULL,
        [FiredAt]          datetimeoffset    NULL,
        [Description]      nvarchar(4000)    NOT NULL,
        [SearchResultsUrl] nvarchar(2048)    NOT NULL,
        [Comments]         nvarchar(4000)     NOT NULL
            CONSTRAINT [DF_Alerts_Comments] DEFAULT N'',
        [RawJson]          nvarchar(max)     NOT NULL,
        CONSTRAINT [PK_Alerts] PRIMARY KEY ([Id])
    );
END;
GO

IF COL_LENGTH(N'[dbo].[Alerts]', N'Comments') IS NULL
BEGIN
    ALTER TABLE [dbo].[Alerts]
        ADD [Comments] nvarchar(4000) NOT NULL
            CONSTRAINT [DF_Alerts_Comments] DEFAULT N'';
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [name] = N'IX_Alerts_AlertId_MonitorCondition'
      AND [object_id] = OBJECT_ID(N'[dbo].[Alerts]')
)
BEGIN
    CREATE INDEX [IX_Alerts_AlertId_MonitorCondition]
        ON [dbo].[Alerts] ([AlertId], [MonitorCondition]);
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [name] = N'IX_Alerts_ReceivedAt'
      AND [object_id] = OBJECT_ID(N'[dbo].[Alerts]')
)
BEGIN
    CREATE INDEX [IX_Alerts_ReceivedAt]
        ON [dbo].[Alerts] ([ReceivedAt]);
END;
GO

IF OBJECT_ID(N'[dbo].[__EFMigrationsHistory]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[__EFMigrationsHistory]
    (
        [MigrationId]    nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32)  NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820185005_InitialAlertSchema'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260820185005_InitialAlertSchema', N'9.0.19');
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821094736_AddAlertComments'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260821094736_AddAlertComments', N'9.0.19');
END;
GO

/*
    Optional: grant the App Service user-assigned managed identity runtime access.
    Replace <managed-identity-name>, remove the comment markers, and execute
    this block as the Microsoft Entra administrator of the SQL server.

CREATE USER [<managed-identity-name>] FROM EXTERNAL PROVIDER;
ALTER ROLE [db_datareader] ADD MEMBER [<managed-identity-name>];
ALTER ROLE [db_datawriter] ADD MEMBER [<managed-identity-name>];
*/

SELECT
    DB_NAME() AS [DatabaseName],
    OBJECT_ID(N'[dbo].[Alerts]', N'U') AS [AlertsTableObjectId],
    (SELECT COUNT(*) FROM [dbo].[__EFMigrationsHistory]) AS [AppliedMigrations];
GO